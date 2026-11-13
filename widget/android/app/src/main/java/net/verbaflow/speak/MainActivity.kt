package net.verbaflow.speak

import android.Manifest
import android.app.Activity
import android.content.ActivityNotFoundException
import android.net.Uri
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.text.InputType
import android.util.TypedValue
import android.view.View
import android.view.WindowInsets
import android.view.inputmethod.InputMethodManager
import android.widget.AdapterView
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Spinner
import android.widget.TextView
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * The VerbaFlow app: VerbaFlow's address, signing in, the language, the microphone, and how to switch the keyboard
 * on, with a box to try it.
 */
class MainActivity : Activity() {
    private val scope = MainScope()
    private lateinit var prefs: Prefs
    private lateinit var signIn: SignIn

    private lateinit var address: EditText
    private lateinit var signInState: TextView
    private lateinit var signInButton: Button
    private lateinit var signOutButton: Button
    private lateinit var linkButton: Button
    private lateinit var linkCode: TextView
    private var linking: kotlinx.coroutines.Job? = null
    private lateinit var testUserRow: LinearLayout
    private lateinit var testUser: EditText
    private lateinit var language: Spinner
    private lateinit var micState: TextView
    private lateinit var micButton: Button
    private lateinit var keyboardState: TextView
    private lateinit var note: TextView
    private var info: SignInInfo? = null
    private var codes: List<String> = emptyList()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = Prefs(this)
        signIn = SignIn(prefs)
        title = "VerbaFlow keyboard"
        setContentView(build())
        handle(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handle(intent)
    }

    override fun onResume() {
        super.onResume()
        refresh()
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    private fun build(): View {
        val page = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(12), dp(16), dp(24))
        }
        note = TextView(this).apply { setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f); visibility = View.GONE }
        page.addView(note)

        page.addView(heading("1. VerbaFlow's address"))
        address = EditText(this).apply {
            hint = "https://verbaflow.example.com"
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            setSingleLine()
            setText(prefs.server)
        }
        page.addView(address)
        page.addView(button("Save address") {
            val url = Prefs.cleanServer(address.text.toString())
            if (url == null) say("That is not a web address. Type it as it shows in your browser, for example https://verbaflow.example.com")
            else {
                prefs.server = url.toString().trimEnd('/')
                address.setText(prefs.server)
                info = null
                say("Address saved.")
                refresh()
            }
        })

        page.addView(heading("2. Sign in"))
        signInState = text("")
        page.addView(signInState)
        signInButton = button("Sign in with Microsoft") { startSignIn() }
        page.addView(signInButton)
        linkButton = button("Google or another personal account: sign in through the VerbaFlow website") { startLink() }
        linkButton.visibility = View.GONE
        page.addView(linkButton)
        linkCode = text("")
        linkCode.visibility = View.GONE
        page.addView(linkCode)
        signOutButton = button("Sign out") { signIn.forget(); say("Signed out."); refresh() }
        page.addView(signOutButton)
        testUserRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; visibility = View.GONE }
        testUser = EditText(this).apply { setSingleLine(); setText(prefs.testUser); hint = "alice" }
        testUserRow.addView(text("Test user: "))
        testUserRow.addView(testUser, LinearLayout.LayoutParams(0, -2, 1f))
        testUserRow.addView(button("Use") { prefs.testUser = testUser.text.toString(); say("Test user saved."); refresh() })
        page.addView(testUserRow)

        page.addView(heading("3. Language you speak"))
        language = Spinner(this)
        page.addView(language)

        page.addView(heading("4. Microphone"))
        micState = text("")
        page.addView(micState)
        micButton = button("Allow the microphone") {
            requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), 1)
        }
        page.addView(micButton)

        page.addView(heading("5. Switch the keyboard on"))
        keyboardState = text("")
        page.addView(keyboardState)
        page.addView(button("Open keyboard settings") {
            startActivity(Intent(Settings.ACTION_INPUT_METHOD_SETTINGS))
        })
        page.addView(button("Choose the keyboard") {
            (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).showInputMethodPicker()
        })

        page.addView(heading("6. Try it"))
        page.addView(text("Tap the box, choose the VerbaFlow keyboard (🌐 or the keyboard button at the bottom of the screen), tap Speak and talk."))
        page.addView(EditText(this).apply {
            hint = "Type or speak here"
            minLines = 4
            gravity = android.view.Gravity.TOP
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_FLAG_CAP_SENTENCES
        })

        val scroll = ScrollView(this).apply { addView(page) }
        // Android 15 draws apps under the status and navigation bars; keep the page clear of them.
        if (Build.VERSION.SDK_INT >= 30) {
            scroll.setOnApplyWindowInsetsListener { v, insets ->
                val bars = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.ime())
                v.setPadding(bars.left, bars.top, bars.right, bars.bottom)
                insets
            }
        }
        return scroll
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (grantResults.firstOrNull() != PackageManager.PERMISSION_GRANTED && !shouldShowRequestPermissionRationale(Manifest.permission.RECORD_AUDIO)) {
            say("Android will not ask again. Open Settings, Apps, VerbaFlow, Permissions, Microphone, and choose Allow only while using the app.")
        }
        refresh()
    }

    private fun api(): Api? = prefs.serverUrl()?.let { Api(it, signIn) }

    /** Shows where each step stands. */
    private fun refresh() {
        val micOk = checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
        micState.text = if (micOk) "Allowed ✓" else "Not allowed yet. The keyboard cannot hear you until you allow it."
        micButton.visibility = if (micOk) View.GONE else View.VISIBLE
        val imm = getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager
        val on = imm.enabledInputMethodList.any { it.packageName == packageName }
        keyboardState.text = if (on) "Switched on ✓" else "Not switched on yet. Open keyboard settings, then turn on VerbaFlow."

        val a = api()
        if (a == null) {
            signInState.text = "Type VerbaFlow's address first."
            signInButton.visibility = View.GONE
            signOutButton.visibility = View.GONE
            linkButton.visibility = View.GONE
            testUserRow.visibility = View.GONE
            return
        }
        scope.launch {
            try {
                val i = a.signInInfo()
                info = i
                signInButton.visibility = if (i.microsoft != null) View.VISIBLE else View.GONE
                signOutButton.visibility = if (signIn.isSignedIn) View.VISIBLE else View.GONE
                linkButton.visibility = if (i.personal && !signIn.isSignedIn) View.VISIBLE else View.GONE
                if (signIn.isSignedIn) signInButton.visibility = View.GONE
                testUserRow.visibility = if (i.testUsers && !signIn.isSignedIn) View.VISIBLE else View.GONE
                if (i.microsoft == null && !i.testUsers && !signIn.isLinked) {
                    signInState.text = "This VerbaFlow has no sign-in set up. Ask your administrator."
                    return@launch
                }
                if (i.microsoft != null && !signIn.isSignedIn && !i.testUsers) {
                    signInState.text = "Not signed in."
                    return@launch
                }
                val name = a.me()
                signInState.text = if (signIn.isLinked) "Signed in as $name (personal account) ✓"
                    else if (signIn.isSignedIn) "Signed in as $name ✓" else "Using the test user $name ✓"
                loadLanguages(a)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                signInState.text = (e as? PlainError)?.message ?: PlainError.UNEXPECTED
            }
        }
    }

    private suspend fun loadLanguages(a: Api) {
        val list = a.languages().filter { it.supported && it.live }
        if (list.isEmpty() || list.map { it.code } == codes) return
        codes = list.map { it.code }
        language.onItemSelectedListener = null
        language.adapter = ArrayAdapter(this, android.R.layout.simple_spinner_item, list.map { it.name }).apply {
            setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item)
        }
        val at = codes.indexOf(prefs.languages)
        if (at >= 0) language.setSelection(at, false)
        else prefs.languages = codes.firstOrNull { it == "en-GB" } ?: codes[0]
        language.onItemSelectedListener = object : AdapterView.OnItemSelectedListener {
            override fun onItemSelected(parent: AdapterView<*>?, view: View?, position: Int, id: Long) {
                prefs.languages = codes[position]
            }
            override fun onNothingSelected(parent: AdapterView<*>?) {}
        }
    }

    private fun startSignIn() {
        val a = api() ?: return
        scope.launch {
            try {
                val ms = a.signInInfo().microsoft ?: return@launch
                startActivity(Intent(Intent.ACTION_VIEW, signIn.startUrl(ms)))
            } catch (e: ActivityNotFoundException) {
                say("No web browser was found on this phone to sign in with.")
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                say((e as? PlainError)?.message ?: PlainError.UNEXPECTED)
            }
        }
    }

    /**
     * Personal accounts (Google and others): the website shows the same code, the person agrees there, and the app
     * collects VerbaFlow's own sign-in. The code lasts 10 minutes.
     */
    private fun startLink() {
        val a = api() ?: return
        linking?.cancel()
        linking = scope.launch {
            try {
                val link = a.startLink("Android keyboard")
                linkCode.text = "Your code is ${link.code}. Sign in on the VerbaFlow website that has just opened and agree to this code there. Then come back here."
                linkCode.visibility = View.VISIBLE
                val page = Uri.parse(a.server.toString().trimEnd('/') + "/#/link-widget?code=" + Uri.encode(link.code))
                try { startActivity(Intent(Intent.ACTION_VIEW, page)) }
                catch (e: ActivityNotFoundException) { say("No web browser was found on this phone. Open VerbaFlow's website on another device and type the code there.") }
                val until = System.currentTimeMillis() + link.expiresInSeconds * 1000L
                while (System.currentTimeMillis() < until) {
                    kotlinx.coroutines.delay(2000)
                    val token = a.collectLink(link.secret) ?: continue
                    signIn.keepLinked(a.server.toString(), token)
                    linkCode.visibility = View.GONE
                    say("Signed in.")
                    refresh()
                    return@launch
                }
                linkCode.visibility = View.GONE
                say("The code ran out before it was agreed on the website. Try again.")
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                linkCode.visibility = View.GONE
                say((e as? PlainError)?.message ?: PlainError.UNEXPECTED)
            }
        }
    }

    /** Microsoft sent the person back from the browser. */
    private fun handle(intent: Intent?) {
        val back = intent?.data ?: return
        if (back.scheme != "net.verbaflow.speak" || back.host != "auth") return
        setIntent(Intent(this, MainActivity::class.java))
        val a = api() ?: return
        scope.launch {
            try {
                val ms = a.signInInfo().microsoft ?: throw PlainError("This VerbaFlow does not use Microsoft sign-in.")
                signIn.finish(ms, back)
                say("Signed in.")
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                say((e as? PlainError)?.message ?: PlainError.UNEXPECTED)
            }
            refresh()
        }
    }

    private fun say(s: String) {
        note.text = s
        note.visibility = View.VISIBLE
    }

    private fun heading(s: String) = TextView(this).apply {
        text = s
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 18f)
        setTypeface(typeface, Typeface.BOLD)
        setPadding(0, dp(20), 0, dp(4))
    }

    private fun text(s: String) = TextView(this).apply { text = s; setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f) }

    private fun button(label: String, onTap: () -> Unit) = Button(this).apply {
        text = label
        isAllCaps = false
        setOnClickListener { onTap() }
    }

    private fun dp(v: Int) = (v * resources.displayMetrics.density + 0.5f).toInt()
}
