package net.verbaflow.speak

import android.Manifest
import android.annotation.SuppressLint
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.inputmethodservice.InputMethodService
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.text.SpannableString
import android.text.Spanned
import android.text.method.ScrollingMovementMethod
import android.text.style.ForegroundColorSpan
import android.util.TypedValue
import android.view.Gravity
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.LinearLayout
import android.widget.TextView
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * The VerbaFlow keyboard. Tap Speak and talk: the words show in the keyboard as they are heard (spoken commands,
 * saved text and voice corrections applied by VerbaFlow). Stop, then Accept types them into the box, or Speak more,
 * Fix grammar or Cancel. Android lets a keyboard use the microphone itself, so no other app needs to open.
 */
class VerbaFlowIme : InputMethodService() {
    private enum class Phase { Idle, Starting, Listening, Finishing, Checking, Fixing, Failed }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var prefs: Prefs
    private lateinit var signIn: SignIn
    private val mic = Microphone()
    private var api: Api? = null
    private var listener: LiveListener? = null
    private var widgetId: String? = null
    private var startJob: Job? = null

    private var phase = Phase.Idle
    private var text = ""
    /** The words heard before "Speak more". */
    private var before = ""
    private var pending = 0
    private var fixedFrom: String? = null
    private var message: String? = null
    private var linkLost = false

    private var preview: TextView? = null
    private var note: TextView? = null
    private var actions: LinearLayout? = null
    private var colors = Colors(false)

    private class Colors(dark: Boolean) {
        val background = if (dark) Color.rgb(0x1E, 0x1F, 0x24) else Color.rgb(0xE9, 0xEB, 0xEF)
        val key = if (dark) Color.rgb(0x3A, 0x3C, 0x43) else Color.WHITE
        val text = if (dark) Color.rgb(0xF2, 0xF3, 0xF5) else Color.rgb(0x1A, 0x1C, 0x22)
        val quiet = if (dark) Color.rgb(0x9A, 0x9E, 0xA8) else Color.rgb(0x6B, 0x70, 0x7B)
        val accent = Color.rgb(0x1F, 0x4F, 0xD1)
        val red = Color.rgb(0xC6, 0x28, 0x28)
        val panel = if (dark) Color.rgb(0x2A, 0x2C, 0x32) else Color.rgb(0xF7, 0xF8, 0xFA)
    }

    override fun onCreate() {
        super.onCreate()
        prefs = Prefs(this)
        signIn = SignIn(prefs)
    }

    override fun onEvaluateFullscreenMode(): Boolean = false

    override fun onCreateInputView(): View {
        val dark = (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) == Configuration.UI_MODE_NIGHT_YES
        colors = Colors(dark)
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(colors.background)
            setPadding(dp(6), dp(6), dp(6), dp(6))
        }
        preview = TextView(this).apply {
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 16f)
            setTextColor(colors.text)
            minLines = 3
            maxLines = 5
            movementMethod = ScrollingMovementMethod()
            isVerticalScrollBarEnabled = true
            setPadding(dp(10), dp(8), dp(10), dp(8))
            background = rounded(colors.panel)
        }
        root.addView(preview, LinearLayout.LayoutParams(-1, -2))
        note = TextView(this).apply {
            setTextSize(TypedValue.COMPLEX_UNIT_SP, 13f)
            setTextColor(colors.quiet)
            setPadding(dp(4), dp(4), dp(4), 0)
        }
        root.addView(note, LinearLayout.LayoutParams(-1, -2))
        actions = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        root.addView(actions, LinearLayout.LayoutParams(-1, dp(52)).apply { topMargin = dp(6) })

        val keys = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }
        keys.addView(key("🌐", 1f) { nextKeyboard() }.apply {
            setOnLongClickListener { picker(); true }
            contentDescription = "Next keyboard"
        })
        keys.addView(key("⚙", 1f) { openApp() }.apply { contentDescription = "VerbaFlow settings" })
        keys.addView(key("space", 3f) { currentInputConnection?.commitText(" ", 1) })
        keys.addView(repeating(key("⌫", 1f) { backspace() }).apply { contentDescription = "Delete" })
        keys.addView(key("⏎", 1f) { enter() }.apply { contentDescription = "Return" })
        root.addView(keys, LinearLayout.LayoutParams(-1, dp(48)).apply { topMargin = dp(6) })
        render()
        return root
    }

    override fun onStartInputView(info: EditorInfo?, restarting: Boolean) {
        super.onStartInputView(info, restarting)
        render()
    }

    /** The keyboard was put away: stop listening, so the microphone is never left on. */
    override fun onWindowHidden() {
        super.onWindowHidden()
        if (phase == Phase.Starting || phase == Phase.Listening || phase == Phase.Finishing) cancel()
    }

    override fun onDestroy() {
        if (phase != Phase.Idle) cancel()
        mic.stop()
        scope.cancel()
        super.onDestroy()
    }

    // ---- listening ----

    private fun api(): Api {
        val url = prefs.serverUrl()
            ?: throw PlainError("Open the VerbaFlow app and type VerbaFlow's address first.", signIn = true)
        api?.let { if (it.server == url) return it }
        return Api(url, signIn).also { api = it }
    }

    private fun isPassword(info: EditorInfo): Boolean {
        val cls = info.inputType and InputType.TYPE_MASK_CLASS
        val v = info.inputType and InputType.TYPE_MASK_VARIATION
        return (cls == InputType.TYPE_CLASS_TEXT && (v == InputType.TYPE_TEXT_VARIATION_PASSWORD ||
            v == InputType.TYPE_TEXT_VARIATION_WEB_PASSWORD || v == InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD)) ||
            (cls == InputType.TYPE_CLASS_NUMBER && v == InputType.TYPE_NUMBER_VARIATION_PASSWORD)
    }

    private fun speak(again: Boolean) {
        if (phase in setOf(Phase.Starting, Phase.Listening, Phase.Finishing, Phase.Fixing)) return
        val info = currentInputEditorInfo
        if (info == null || info.inputType == InputType.TYPE_NULL || isPassword(info)) {
            message = "The VerbaFlow keyboard does not listen in password boxes."
            render()
            return
        }
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            message = "VerbaFlow may not use the microphone yet. Tap ⚙ and allow the microphone."
            render()
            return
        }
        before = if (again) text else ""
        if (!again) { text = ""; fixedFrom = null }
        phase = Phase.Starting
        message = null
        pending = 0
        linkLost = false
        render()
        startJob = scope.launch {
            try {
                val a = api()
                val id = widgetId
                val start = if (again && id != null) a.listenAgain(id) else {
                    if (id != null) try { a.end(id, false, null, null, null) } catch (_: PlainError) { }
                    a.start(DictationText.siteOf(info.packageName), DEVICE, prefs.languages)
                }
                widgetId = start.id
                try { mic.start() } catch (e: Exception) {
                    throw PlainError("The microphone could not be started. Close other apps that may be using it, then try again.")
                }
                val l = LiveListener(a, start, scope)
                l.heard = { h -> onHeard(h) }
                l.link = { up -> linkLost = !up; render() }
                l.failed = { m -> onFailed(m) }
                l.ended = { _, last -> onEnded(last) }
                listener = l
                mic.sound = { pcm -> l.write(pcm) }
                l.start()
                phase = Phase.Listening
                render()
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                onFailed((e as? PlainError)?.message ?: PlainError.UNEXPECTED)
            }
        }
    }

    private fun stopListening() {
        if (phase == Phase.Starting) { cancel(); return }
        if (phase != Phase.Listening) return
        mic.sound = null
        mic.stop()
        phase = Phase.Finishing
        render()
        listener?.stop()
    }

    private fun onHeard(h: LiveListener.Heard) {
        if (phase != Phase.Listening && phase != Phase.Finishing) return
        text = DictationText.join(before, DictationText.heard(h.segments, h.startsWithBreak))
        pending = h.pendingWords
        render()
    }

    private fun onEnded(last: LiveListener.Heard?) {
        mic.sound = null
        mic.stop()
        if (phase != Phase.Listening && phase != Phase.Finishing) return
        last?.let { onHeard(it) }
        pending = 0
        phase = if (text.isBlank() && before.isEmpty()) Phase.Idle else Phase.Checking
        if (phase == Phase.Idle) message = "Nothing was heard. Try again a little closer to the microphone."
        render()
    }

    private fun onFailed(m: String) {
        mic.sound = null
        mic.stop()
        listener?.close()
        listener = null
        pending = 0
        phase = if (text.isEmpty()) Phase.Failed else Phase.Checking
        message = m
        render()
    }

    private fun accept() {
        val ic = currentInputConnection ?: return
        val t = text
        if (t.isNotBlank()) {
            val beforeCursor = ic.getTextBeforeCursor(1, 0)
            val out = if (beforeCursor.isNullOrEmpty()) t.trimStart('\n') else DictationText.spaced(beforeCursor, t)
            ic.beginBatchEdit()
            ic.commitText(out, 1)
            ic.endBatchEdit()
        }
        end(true, DictationText.words(t))
    }

    private fun cancel() = end(false, null)

    private fun end(accepted: Boolean, words: Int?) {
        startJob?.cancel()
        startJob = null
        mic.sound = null
        mic.stop()
        val l = listener
        listener = null
        l?.close()
        val id = widgetId
        widgetId = null
        phase = Phase.Idle
        text = ""
        before = ""
        fixedFrom = null
        pending = 0
        message = null
        linkLost = false
        render()
        // Only counts are kept in VerbaFlow's history; what was said is thrown away there.
        if (id != null) {
            val a = try { api() } catch (_: PlainError) { null } ?: return
            scope.launch {
                try { a.end(id, accepted, l?.spokenMs, words, l?.reconnects) } catch (_: Exception) { }
            }
        }
    }

    private fun fixGrammar() {
        val id = widgetId ?: return
        if (phase != Phase.Checking || text.isBlank()) return
        val original = text
        phase = Phase.Fixing
        message = null
        render()
        scope.launch {
            try {
                val fix = api().fixGrammar(id, original)
                if (phase != Phase.Fixing) return@launch
                phase = Phase.Checking
                if (fix.text == original) message = "No changes were needed."
                else { fixedFrom = original; text = fix.text }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                if (phase != Phase.Fixing) return@launch
                phase = Phase.Checking
                message = (e as? PlainError)?.message ?: PlainError.UNEXPECTED
            }
            render()
        }
    }

    private fun undoFix() {
        val from = fixedFrom ?: return
        text = from
        fixedFrom = null
        message = null
        render()
    }

    // ---- ordinary keys ----

    private fun nextKeyboard() {
        val switched = if (Build.VERSION.SDK_INT >= 28) switchToNextInputMethod(false) else {
            @Suppress("DEPRECATION")
            val token = window?.window?.attributes?.token
            token != null && (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).switchToNextInputMethod(token, false)
        }
        if (!switched) picker()
    }

    private fun picker() = (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).showInputMethodPicker()

    private fun openApp() {
        startActivity(Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }

    private fun backspace() {
        val ic = currentInputConnection ?: return
        if (!ic.getSelectedText(0).isNullOrEmpty()) ic.commitText("", 1)
        else sendDownUpKeyEvents(KeyEvent.KEYCODE_DEL)
    }

    private fun enter() {
        val ic = currentInputConnection ?: return
        val info = currentInputEditorInfo
        val action = (info?.imeOptions ?: 0) and EditorInfo.IME_MASK_ACTION
        val noAction = info == null || (info.imeOptions and EditorInfo.IME_FLAG_NO_ENTER_ACTION) != 0 ||
            action == EditorInfo.IME_ACTION_NONE || action == EditorInfo.IME_ACTION_UNSPECIFIED
        if (noAction) sendDownUpKeyEvents(KeyEvent.KEYCODE_ENTER) else ic.performEditorAction(action)
    }

    // ---- drawing ----

    private fun render() {
        val preview = preview ?: return
        val note = note ?: return
        val actions = actions ?: return
        val shown: CharSequence = when (phase) {
            Phase.Idle -> grey("Tap Speak, then talk. Your words appear here.")
            Phase.Failed -> grey("Tap Speak to try again.")
            Phase.Starting -> if (text.isEmpty()) grey("Getting ready…") else text
            else -> if (text.isEmpty()) grey(if (phase == Phase.Listening) "Listening…" else "…") else withPending()
        }
        preview.text = shown
        preview.post {
            val layout = preview.layout ?: return@post
            val scroll = layout.getLineTop(preview.lineCount) - preview.height + preview.paddingTop + preview.paddingBottom
            preview.scrollTo(0, maxOf(0, scroll))
        }
        val lines = mutableListOf<String>()
        if (linkLost && (phase == Phase.Listening || phase == Phase.Finishing))
            lines.add("Connection lost. Still listening; the words will catch up.")
        if (phase == Phase.Finishing) lines.add("Finishing the last words…")
        if (phase == Phase.Fixing) lines.add("Fixing grammar…")
        message?.let { lines.add(it) }
        note.text = lines.joinToString("\n")
        note.setTextColor(if (phase == Phase.Failed) colors.red else colors.quiet)
        note.visibility = if (lines.isEmpty()) View.GONE else View.VISIBLE

        actions.removeAllViews()
        when (phase) {
            Phase.Idle, Phase.Failed -> actions.addView(key("🎤  Speak", 1f, primary = true) { speak(false) })
            Phase.Starting, Phase.Finishing, Phase.Fixing -> actions.addView(key("Cancel", 1f) { cancel() })
            Phase.Listening -> {
                actions.addView(key("■  Stop", 2f, primary = true, red = true) { stopListening() })
                actions.addView(key("Cancel", 1f) { cancel() })
            }
            Phase.Checking -> {
                actions.addView(key("Accept", 1.2f, primary = true) { accept() })
                actions.addView(key("Speak more", 1.3f) { speak(true) })
                if (fixedFrom != null) actions.addView(key("Undo fix", 1.2f) { undoFix() })
                else actions.addView(key("Fix grammar", 1.3f) { fixGrammar() })
                actions.addView(key("Cancel", 1f) { cancel() })
            }
        }
    }

    private fun grey(s: String) = SpannableString(s).apply {
        setSpan(ForegroundColorSpan(colors.quiet), 0, s.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
    }

    /** The text, with the words still being heard in grey. */
    private fun withPending(): CharSequence {
        if (pending <= 0) return text
        val from = DictationText.pendingStart(text, pending)
        return SpannableString(text).apply {
            setSpan(ForegroundColorSpan(colors.quiet), from, text.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        }
    }

    private fun key(label: String, weight: Float, primary: Boolean = false, red: Boolean = false, onTap: () -> Unit): TextView =
        TextView(this).apply {
            text = label
            gravity = Gravity.CENTER
            setTextSize(TypedValue.COMPLEX_UNIT_SP, if (label.length <= 2) 20f else 15f)
            setTextColor(if (primary) Color.WHITE else colors.text)
            if (primary) setTypeface(typeface, Typeface.BOLD)
            background = rounded(if (red) colors.red else if (primary) colors.accent else colors.key)
            isClickable = true
            isFocusable = false
            maxLines = 1
            setOnClickListener { onTap() }
            layoutParams = LinearLayout.LayoutParams(0, -1, weight).apply { setMargins(dp(3), 0, dp(3), 0) }
        }

    /** Holding the key repeats it, as on other keyboards. */
    @SuppressLint("ClickableViewAccessibility")
    private fun repeating(view: TextView): TextView {
        val handler = Handler(Looper.getMainLooper())
        val again = object : Runnable {
            override fun run() { view.performClick(); handler.postDelayed(this, 60) }
        }
        view.setOnTouchListener { v, e ->
            when (e.action) {
                MotionEvent.ACTION_DOWN -> { v.isPressed = true; view.performClick(); handler.postDelayed(again, 450) }
                MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> { v.isPressed = false; handler.removeCallbacks(again) }
            }
            true
        }
        return view
    }

    private fun rounded(color: Int) = GradientDrawable().apply {
        setColor(color)
        cornerRadius = dp(8).toFloat()
    }

    private fun dp(v: Int) = (v * resources.displayMetrics.density + 0.5f).toInt()

    companion object {
        /** What VerbaFlow's history shows as the device. */
        const val DEVICE = "Android keyboard"
    }
}
