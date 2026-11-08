package net.verbaflow.speak

import android.net.Uri
import android.util.Base64
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.FormBody
import okhttp3.Request
import org.json.JSONException
import org.json.JSONObject
import java.io.IOException
import java.security.MessageDigest
import java.security.SecureRandom

/**
 * Signs in with a Microsoft work account (authorisation code with PKCE, in the phone's browser), or uses a test user
 * when this VerbaFlow allows them (the owner's own computer only). Tokens stay in this app's private storage and go
 * only to Microsoft and VerbaFlow.
 */
class SignIn(private val prefs: Prefs) {
    companion object {
        /** Registered in Microsoft Entra under "Mobile and desktop applications". */
        const val REDIRECT = "net.verbaflow.speak://auth"

        private fun random(): String {
            val b = ByteArray(32)
            SecureRandom().nextBytes(b)
            return b64(b)
        }

        private fun b64(b: ByteArray) = Base64.encodeToString(b, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)
    }

    val isSignedIn: Boolean get() = prefs.access.isNotEmpty() || prefs.linked.isNotEmpty()

    /** Signed in with a personal account through the VerbaFlow website (Google and others). */
    val isLinked: Boolean get() = prefs.linked.isNotEmpty()
    val linkedRenewed: Long get() = prefs.linkedRenewed

    fun linkedFor(server: String): String? =
        prefs.linked.takeIf { it.isNotEmpty() && prefs.linkedServer.trimEnd('/') == server.trimEnd('/') }

    fun keepLinked(server: String, token: String) {
        prefs.linked = token
        prefs.linkedServer = server.trimEnd('/')
        prefs.linkedRenewed = System.currentTimeMillis()
    }

    private val needed get() = PlainError("Open the VerbaFlow app and sign in first.", signIn = true)

    /** The header to send: a Microsoft token, or X-Dev-User for a test user. */
    suspend fun header(info: SignInInfo): Pair<String, String> {
        val ms = info.microsoft
        if (ms != null && prefs.access.isNotEmpty()) {
            if (prefs.expires - System.currentTimeMillis() < 120_000) {
                val refresh = prefs.refresh
                if (refresh.isEmpty()) { forget(); throw needed }
                token(ms, mapOf("grant_type" to "refresh_token", "refresh_token" to refresh))
            }
            return "Authorization" to "Bearer ${prefs.access}"
        }
        if (info.testUsers) return "X-Dev-User" to prefs.testUser
        throw needed
    }

    fun forget() = prefs.forgetSignIn()

    /** Microsoft's sign-in page, to open in the browser. Microsoft then sends the person back to [REDIRECT]. */
    fun startUrl(ms: MicrosoftInfo): Uri {
        val verifier = random()
        val state = random()
        prefs.verifier = verifier
        prefs.state = state
        val challenge = b64(MessageDigest.getInstance("SHA-256").digest(verifier.toByteArray(Charsets.US_ASCII)))
        return Uri.parse("${ms.authority}/oauth2/v2.0/authorize").buildUpon()
            .appendQueryParameter("client_id", ms.clientId)
            .appendQueryParameter("response_type", "code")
            .appendQueryParameter("redirect_uri", REDIRECT)
            .appendQueryParameter("response_mode", "query")
            .appendQueryParameter("scope", "${ms.scope} offline_access openid profile")
            .appendQueryParameter("code_challenge", challenge)
            .appendQueryParameter("code_challenge_method", "S256")
            .appendQueryParameter("state", state)
            .appendQueryParameter("prompt", "select_account")
            .build()
    }

    /** Microsoft sent the person back: swap the code for tokens. */
    suspend fun finish(ms: MicrosoftInfo, back: Uri) {
        val state = prefs.state
        val verifier = prefs.verifier
        prefs.state = ""
        prefs.verifier = ""
        val code = back.getQueryParameter("code")
        if (state.isEmpty() || back.getQueryParameter("state") != state || code.isNullOrEmpty())
            throw PlainError("Microsoft did not sign you in. Try again, or ask your administrator.")
        token(ms, mapOf("grant_type" to "authorization_code", "code" to code, "code_verifier" to verifier))
    }

    private suspend fun token(ms: MicrosoftInfo, fields: Map<String, String>) {
        val form = FormBody.Builder()
        fields.forEach { (k, v) -> form.add(k, v) }
        form.add("client_id", ms.clientId)
        form.add("redirect_uri", REDIRECT)
        form.add("scope", "${ms.scope} offline_access openid profile")
        val req = Request.Builder().url("${ms.authority}/oauth2/v2.0/token").post(form.build()).build()
        val text = withContext(Dispatchers.IO) {
            try { Http.client.newCall(req).execute().use { it.body?.string() ?: "" } }
            catch (e: IOException) { throw PlainError(PlainError.UNREACHABLE) }
        }
        val o = try { JSONObject(text) } catch (e: JSONException) { JSONObject() }
        val access = o.optString("access_token")
        if (access.isEmpty()) {
            forget()
            throw PlainError("Microsoft did not sign you in. Open the VerbaFlow app and sign in again.", signIn = true)
        }
        prefs.access = access
        o.optString("refresh_token").takeIf { it.isNotEmpty() }?.let { prefs.refresh = it }
        prefs.expires = System.currentTimeMillis() + o.optLong("expires_in", 3600) * 1000
    }
}
