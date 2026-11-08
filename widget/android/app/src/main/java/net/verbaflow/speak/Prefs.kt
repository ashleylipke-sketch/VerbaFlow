package net.verbaflow.speak

import android.content.Context
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

/**
 * What the app remembers, in this app's private storage on the phone (no other app can read it, and it is not
 * backed up): VerbaFlow's address, the language, the test user, and the Microsoft sign-in.
 */
class Prefs(context: Context) {
    private val p = context.applicationContext.getSharedPreferences("verbaflow", Context.MODE_PRIVATE)

    private fun str(key: String, default: String = "") = p.getString(key, default) ?: default
    private fun put(key: String, value: String) = p.edit().putString(key, value).apply()

    var server: String get() = str("server"); set(v) = put("server", v)
    var languages: String get() = str("languages", "en-GB"); set(v) = put("languages", v)
    var testUser: String get() = str("testUser", "alice"); set(v) = put("testUser", v.trim().lowercase())

    var access: String get() = str("access"); set(v) = put("access", v)
    var refresh: String get() = str("refresh"); set(v) = put("refresh", v)
    var expires: Long get() = p.getLong("expires", 0); set(v) = p.edit().putLong("expires", v).apply()

    /** A VerbaFlow sign-in for a personal account (Google and others), agreed on the VerbaFlow website. */
    var linked: String get() = str("linked"); set(v) = put("linked", v)
    var linkedServer: String get() = str("linkedServer"); set(v) = put("linkedServer", v)
    var linkedRenewed: Long get() = p.getLong("linkedRenewed", 0); set(v) = p.edit().putLong("linkedRenewed", v).apply()

    /** Kept only while Microsoft's sign-in page is open in the browser. */
    var verifier: String get() = str("verifier"); set(v) = put("verifier", v)
    var state: String get() = str("state"); set(v) = put("state", v)

    fun forgetSignIn() = p.edit().remove("access").remove("refresh").remove("expires")
        .remove("linked").remove("linkedServer").remove("linkedRenewed").apply()

    fun serverUrl(): HttpUrl? = cleanServer(server)

    companion object {
        /** VerbaFlow's address as typed, or null when it is not one (http or https only). */
        fun cleanServer(typed: String): HttpUrl? {
            var t = typed.trim().trimEnd('/')
            if (t.isEmpty()) return null
            if (!t.contains("://")) t = "https://$t"
            val u = t.toHttpUrlOrNull() ?: return null
            return if (u.scheme == "http" || u.scheme == "https") u else null
        }
    }
}
