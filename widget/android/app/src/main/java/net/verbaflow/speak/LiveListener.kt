package net.verbaflow.speak

import android.os.Handler
import android.os.Looper
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString
import org.json.JSONException
import org.json.JSONObject

/**
 * One listen: the microphone's sound goes to VerbaFlow over a live connection and the text comes back. A lost
 * connection is made again (after 1, 2, 4, 8, then every 10 seconds) and the sound since the end of the last finished
 * phrase is sent again, so nothing is missed or doubled. The sound is held only in memory while listening.
 * Callbacks come on the main thread.
 */
class LiveListener(private val api: Api, start: WidgetStart, private val scope: CoroutineScope) {
    data class Heard(val segments: List<LivePara>, val pendingWords: Int, val startsWithBreak: Boolean)

    var heard: ((Heard) -> Unit)? = null
    /** The connection was lost (false) or is live again (true). Listening carries on either way. */
    var link: ((Boolean) -> Unit)? = null
    /** The live service failed: a plain sentence with a VF- reference. The words heard so far stay. */
    var failed: ((String) -> Unit)? = null
    /** The listen has ended after Stop: whether all the words came back, and the last text. Called once. */
    var ended: ((Boolean, Heard?) -> Unit)? = null

    private val liveId = start.liveId
    private var ticket: String? = start.ticket
    private val lock = Any()
    private val audio = ArrayList<ByteArray>()
    private var bytes = 0L
    private var finalToMs = 0
    private var socket: WebSocket? = null
    private var ready = false
    private var stopping = false
    private var finished = false
    private var failedNow = false
    private var over = false
    private var connections = 0
    private var attempt = 0
    private var last: Heard? = null
    private val main = Handler(Looper.getMainLooper())
    private val giveUp = Runnable { report(false) }

    var reconnects = 0
        private set
    val spokenMs: Int get() = synchronized(lock) { (bytes / BYTES_PER_MS).toInt() }

    fun start() = connect()

    /** Sound from the microphone, 16 kHz 16-bit mono. Called on the microphone's thread. */
    fun write(pcm: ByteArray) {
        if (pcm.isEmpty()) return
        synchronized(lock) {
            if (stopping || finished || over) return
            audio.add(pcm)
            bytes += pcm.size
            if (ready) socket?.send(pcm.toByteString())
        }
    }

    /** The person stopped speaking: the last words are finished and sent back, then [ended] is called. */
    fun stop() {
        synchronized(lock) {
            if (stopping || over) return
            stopping = true
            if (finished || failedNow) { main.post { report(finished) }; return }
            if (ready) socket?.send(END)
        }
        // Without a connection the last words cannot come back; the words heard so far are kept.
        main.postDelayed(giveUp, 15_000)
    }

    /** Ends the listen at once, without [ended], and drops the sound held. */
    fun close() {
        val s = synchronized(lock) {
            over = true
            audio.clear()
            ready = false
            socket.also { socket = null }
        }
        main.removeCallbacks(giveUp)
        s?.cancel()
    }

    private fun report(complete: Boolean) {
        val h = synchronized(lock) {
            if (over) return
            last
        }
        close()
        ended?.invoke(complete, h)
    }

    private fun connect() {
        scope.launch {
            val t = try {
                ticket ?: api.ticket(liveId)
            } catch (e: Exception) {
                retry()
                return@launch
            }
            ticket = null
            val fromMs = synchronized(lock) { if (over) null else finalToMs } ?: return@launch
            val req = Request.Builder().url(api.socketUrl(liveId, t, fromMs)).build()
            synchronized(lock) {
                if (over) return@launch
                socket = Http.client.newWebSocket(req, Connection(fromMs))
                if (connections++ > 0) reconnects++
            }
        }
    }

    private fun retry() {
        val delay = minOf(10_000L, 1000L shl minOf(attempt++, 4))
        main.postDelayed({ if (!synchronized(lock) { over }) connect() }, delay)
    }

    private inner class Connection(private val fromMs: Int) : WebSocketListener() {
        override fun onMessage(webSocket: WebSocket, text: String) {
            val m = try { JSONObject(text) } catch (e: JSONException) { return }
            when (m.optString("type")) {
                "ready" -> {
                    synchronized(lock) {
                        if (webSocket !== socket) return
                        attempt = 0
                        // The sound since the end of the last finished phrase goes again, in pieces of a second.
                        for (piece in from(fromMs * BYTES_PER_MS.toLong())) webSocket.send(piece)
                        ready = true
                        if (stopping) webSocket.send(END)
                    }
                    main.post { link?.invoke(true) }
                }
                "text", "done" -> {
                    val h = heardOf(m)
                    val done = m.optString("type") == "done"
                    synchronized(lock) {
                        if (webSocket !== socket) return
                        finalTo(m)?.let { finalToMs = it }
                        last = h
                        if (done) finished = true
                    }
                    main.post {
                        if (!synchronized(lock) { over }) heard?.invoke(h)
                        if (done) report(true)
                    }
                }
                "error" -> {
                    val stoppingNow = synchronized(lock) {
                        if (webSocket !== socket) return
                        failedNow = true
                        stopping
                    }
                    val message = m.optString("message").ifEmpty { PlainError.UNEXPECTED }
                    main.post {
                        if (!synchronized(lock) { over }) failed?.invoke(message)
                        if (stoppingNow) report(false)
                    }
                }
            }
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(1000, null)
            lost(webSocket)
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = lost(webSocket)

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) = lost(webSocket)
    }

    private fun lost(ws: WebSocket) {
        synchronized(lock) {
            if (ws !== socket) return
            socket = null
            ready = false
            if (finished || failedNow || over) return
        }
        main.post { link?.invoke(false) }
        retry()
    }

    /** The sound held from a byte position on, in pieces of at most one second. Called holding the lock. */
    private fun from(start: Long): List<ByteString> {
        val out = ArrayList<ByteString>()
        var at = 0L
        val second = 1000 * BYTES_PER_MS
        for (chunk in audio) {
            val skip = maxOf(0L, start - at)
            at += chunk.size
            if (skip >= chunk.size) continue
            var i = skip.toInt()
            while (i < chunk.size) {
                val n = minOf(second, chunk.size - i)
                out.add(chunk.toByteString(i, n))
                i += n
            }
        }
        return out
    }

    private fun finalTo(m: JSONObject): Int? = if (m.has("finalToMs") && !m.isNull("finalToMs")) m.optInt("finalToMs") else null

    private fun heardOf(m: JSONObject): Heard {
        val segs = ArrayList<LivePara>()
        m.optJSONArray("segments")?.let { a ->
            for (i in 0 until a.length()) {
                val p = a.optJSONObject(i) ?: continue
                segs.add(
                    LivePara(
                        if (p.isNull("text")) null else p.optString("text"),
                        if (p.has("newParagraph") && !p.isNull("newParagraph")) p.optBoolean("newParagraph") else null,
                        if (p.has("list") && !p.isNull("list")) p.optString("list") else null,
                    )
                )
            }
        }
        return Heard(segs, m.optInt("pendingWords"), m.optBoolean("startsWithBreak"))
    }

    companion object {
        const val BYTES_PER_MS = 32 // 16 000 samples a second, two bytes each
        private const val END = "{\"type\":\"end\"}"
    }
}
