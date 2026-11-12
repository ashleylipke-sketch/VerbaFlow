package net.verbaflow.speak

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONException
import org.json.JSONObject
import java.io.IOException
import java.util.concurrent.TimeUnit

/** A plain sentence for the person. Never technical detail. [signIn] means the app must be opened to fix it. */
class PlainError(message: String, val signIn: Boolean = false) : Exception(message) {
    companion object {
        const val UNREACHABLE = "VerbaFlow cannot be reached. Check your internet connection and the address in the VerbaFlow app."
        const val UNEXPECTED = "Something went wrong. Try again."
    }
}

data class MicrosoftInfo(val tenantId: String, val clientId: String, val scope: String, val authority: String)
data class SignInInfo(val testUsers: Boolean, val microsoft: MicrosoftInfo?, val personal: Boolean = false)
data class WidgetLink(val code: String, val secret: String, val expiresInSeconds: Int)
data class WidgetStart(val id: String, val liveId: String, val ticket: String, val engine: String)
data class Language(val code: String, val name: String, val supported: Boolean, val live: Boolean = true)
data class GrammarFix(val text: String, val linesChanged: Int, val leftOutAsRewrites: Int)

object Http {
    val client: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(90, TimeUnit.SECONDS)
        .pingInterval(15, TimeUnit.SECONDS)
        .build()
    val json = "application/json; charset=utf-8".toMediaType()
}

/** The calls the keyboard makes to VerbaFlow: the same as the other Widgets'. */
class Api(val server: HttpUrl, private val signIn: SignIn) {
    private var info: SignInInfo? = null
    private var infoAt = 0L

    fun url(path: String): HttpUrl = (server.toString().trimEnd('/') + "/" + path).toHttpUrl()

    /** How this VerbaFlow signs people in (public details), remembered for ten minutes. */
    suspend fun signInInfo(): SignInInfo {
        info?.let { if (System.currentTimeMillis() - infoAt < 600_000) return it }
        val text = withContext(Dispatchers.IO) {
            try {
                Http.client.newCall(Request.Builder().url(url("api/sign-in")).build()).execute().use { r ->
                    if (!r.isSuccessful) throw PlainError(PlainError.UNREACHABLE)
                    r.body?.string() ?: ""
                }
            } catch (e: IOException) { throw PlainError(PlainError.UNREACHABLE) }
        }
        try {
            val o = JSONObject(text)
            val ms = o.optJSONObject("microsoft")?.let {
                MicrosoftInfo(it.getString("tenantId"), it.getString("clientId"), it.getString("scope"), it.getString("authority"))
            }
            return SignInInfo(o.optBoolean("testUsers"), ms, o.optBoolean("personalAccounts")).also { info = it; infoAt = System.currentTimeMillis() }
        } catch (e: JSONException) { throw PlainError(PlainError.UNREACHABLE) }
    }

    /** Starts linking this keyboard to a personal account: the person agrees to the code on the VerbaFlow website. */
    suspend fun startLink(device: String): WidgetLink {
        val (code, text) = post("api/widget-link/start", JSONObject().put("device", device), null)
        if (code !in 200..299) throw PlainError(errorOf(text) ?: PlainError.UNEXPECTED)
        val o = try { JSONObject(text) } catch (e: JSONException) { throw PlainError(PlainError.UNEXPECTED) }
        return WidgetLink(o.getString("code"), o.getString("secret"), o.optInt("expiresInSeconds", 600))
    }

    /** The sign-in once the code has been agreed on the website, or null while still waiting. */
    suspend fun collectLink(secret: String): String? {
        val (code, text) = post("api/widget-link/poll", JSONObject().put("secret", secret), null)
        if (code == 202) return null
        if (code !in 200..299) throw PlainError(errorOf(text) ?: PlainError.UNEXPECTED)
        return try { JSONObject(text).getString("token") } catch (e: JSONException) { throw PlainError(PlainError.UNEXPECTED) }
    }

    /** A fresh linked sign-in (they last 30 days), or null when it has run out. Offline: the old one is kept. */
    private suspend fun renewLink(token: String): String? {
        val (code, text) = try { post("api/sign-in/renew", null, token) } catch (e: PlainError) { return token }
        if (code == 401) return null
        if (code !in 200..299) return token
        return try { JSONObject(text).optString("token").ifEmpty { token } } catch (e: JSONException) { token }
    }

    private suspend fun post(path: String, body: JSONObject?, bearer: String?): Pair<Int, String> {
        val b = Request.Builder().url(url(path)).post((body?.toString() ?: "").toRequestBody(Http.json))
        bearer?.let { b.header("Authorization", "Bearer $it") }
        return withContext(Dispatchers.IO) {
            try { Http.client.newCall(b.build()).execute().use { r -> r.code to (r.body?.string() ?: "") } }
            catch (e: IOException) { throw PlainError(PlainError.UNREACHABLE) }
        }
    }

    /** The linked sign-in for this VerbaFlow, renewed once a day. */
    private suspend fun linkedHeader(): Pair<String, String>? {
        val token = signIn.linkedFor(server.toString()) ?: return null
        if (System.currentTimeMillis() - signIn.linkedRenewed > 86_400_000) {
            val fresh = renewLink(token)
            if (fresh == null) {
                signIn.forget()
                throw PlainError("Your sign-in has run out. Open the VerbaFlow app and sign in again.", signIn = true)
            }
            signIn.keepLinked(server.toString(), fresh)
            return "Authorization" to "Bearer $fresh"
        }
        return "Authorization" to "Bearer $token"
    }

    /** Who VerbaFlow says is signed in. */
    suspend fun me(): String = send("GET", "api/me", null).optString("name")

    suspend fun start(site: String, device: String, languages: String): WidgetStart =
        startOf(send("POST", "api/widget/start", JSONObject().put("site", site).put("device", device).put("spokenLanguages", languages)))

    suspend fun listenAgain(id: String): WidgetStart = startOf(send("POST", "api/widget/$id/listen-again", null))

    /** Only counts are sent: VerbaFlow keeps nothing of what was said. */
    suspend fun end(id: String, accepted: Boolean, spokenMs: Int?, words: Int?, reconnects: Int?) {
        val body = JSONObject().put("outcome", if (accepted) "accepted" else "cancelled")
        spokenMs?.let { body.put("spokenMs", it) }
        words?.let { body.put("words", it) }
        reconnects?.let { body.put("reconnects", it) }
        send("POST", "api/widget/$id/end", body)
    }

    suspend fun fixGrammar(id: String, text: String): GrammarFix {
        val o = send("POST", "api/widget/$id/grammar", JSONObject().put("text", text))
        return GrammarFix(o.optString("text", text), o.optInt("linesChanged"), o.optInt("leftOutAsRewrites"))
    }

    /** The languages VerbaFlow can transcribe. */
    suspend fun languages(): List<Language> {
        val a = send("GET", "api/languages", null).optJSONArray("languages") ?: return emptyList()
        return (0 until a.length()).map { a.getJSONObject(it) }
            .map { Language(it.optString("code"), it.optString("name"), it.optBoolean("supported"), it.optBoolean("live", true)) }
    }

    /** A new single-use ticket for the live connection (after a lost connection). */
    suspend fun ticket(liveId: String): String =
        send("POST", "api/live/$liveId/ticket", null).optString("ticket").ifEmpty { throw PlainError(PlainError.UNREACHABLE) }

    /** OkHttp opens ws:// and wss:// connections from http and https addresses. */
    fun socketUrl(liveId: String, ticket: String, fromMs: Int): HttpUrl =
        url("api/live/$liveId/socket").newBuilder()
            .addQueryParameter("ticket", ticket)
            .addQueryParameter("fromMs", fromMs.toString())
            .build()

    private fun startOf(o: JSONObject) = WidgetStart(o.getString("id"), o.getString("liveId"), o.getString("ticket"), o.optString("engine"))

    private suspend fun send(method: String, path: String, body: JSONObject?): JSONObject {
        val header = linkedHeader() ?: signIn.header(signInInfo())
        val req = Request.Builder().url(url(path)).header(header.first, header.second)
            .method(method, if (method == "GET") null else (body?.toString() ?: "").toRequestBody(Http.json))
            .build()
        return withContext(Dispatchers.IO) {
            val (code, text) = try {
                Http.client.newCall(req).execute().use { r -> r.code to (r.body?.string() ?: "") }
            } catch (e: IOException) { throw PlainError(PlainError.UNREACHABLE) }
            if (code == 401) {
                signIn.forget()
                throw PlainError("Your sign-in has run out. Open the VerbaFlow app and sign in again.", signIn = true)
            }
            if (code !in 200..299) throw PlainError(errorOf(text) ?: "Something went wrong in VerbaFlow.")
            if (text.isBlank()) JSONObject()
            else try { JSONObject(text) } catch (e: JSONException) { throw PlainError("Something went wrong in VerbaFlow.") }
        }
    }

    /** The plain sentence VerbaFlow wrote for people, if there is one. */
    private fun errorOf(text: String): String? = try {
        JSONObject(text).optString("error").ifEmpty { null }
    } catch (e: JSONException) { null }
}
