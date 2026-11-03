package net.verbaflow.speak

/** One passage of the live text as VerbaFlow sends it, with spoken commands already applied. */
data class LivePara(val text: String?, val newParagraph: Boolean?, val list: String?)

/**
 * How the heard words become text: paragraphs and list markers as the VerbaFlow item page shows them, the words already
 * in the keyboard, and a space where a person typing would leave one. The same rules as the other Widgets.
 */
object DictationText {
    /** The heard text as paragraphs, with "• " and "1. " list markers. */
    fun paragraphs(segments: List<LivePara>): List<String> {
        val paras = mutableListOf<Pair<String, String?>>()
        var first = true
        for (s in segments) {
            val pieces = (s.text ?: "").replace("\r\n", "\n").split('\n')
            for ((k, piece) in pieces.withIndex()) {
                val p = piece.trim()
                if (first || k > 0 || s.newParagraph == true) paras.add(p to (if (k == 0) s.list else null))
                else {
                    val last = paras.last()
                    paras[paras.size - 1] = "${last.first} $p".trim() to last.second
                }
                first = false
            }
        }
        var n = 0
        val result = mutableListOf<String>()
        for ((text, list) in paras) {
            if (text.isEmpty()) continue
            n = if (list == "number") n + 1 else 0
            result.add(
                when (list) {
                    "bullet" -> "• $text"
                    "number" -> "$n. $text"
                    else -> text
                }
            )
        }
        return result
    }

    /** The heard text, paragraphs separated by an empty line. "new paragraph" said first breaks after the words before it. */
    fun heard(segments: List<LivePara>, startsWithBreak: Boolean): String {
        val h = paragraphs(segments).joinToString("\n\n")
        return if (h.isNotEmpty() && startsWithBreak) "\n\n$h" else h
    }

    /** Whether heard text joins straight on to what is before it (it starts with a space, a break or punctuation). */
    fun joinsOn(heard: String): Boolean =
        heard.isNotEmpty() && (heard[0].isWhitespace() || ".,;:!?)]\"”’".contains(heard[0]))

    /** The words already in the keyboard (from before "Speak more") followed by the newly heard ones. */
    fun join(before: String, heard: String): String {
        val p = before.trimEnd()
        if (heard.isEmpty()) return p.trim()
        if (p.isBlank()) return heard
        return if (joinsOn(heard) || before.endsWith('\n')) p + heard else "$p $heard"
    }

    /** A space between what was already in the box and the new words, as a person typing would leave one. */
    fun spaced(before: CharSequence?, text: String): String =
        if (!before.isNullOrEmpty() && !before.last().isWhitespace() && !joinsOn(text)) " $text" else text

    fun words(text: String): Int = text.split(Regex("\\s+")).count { it.isNotEmpty() }

    /** Where the last [count] words start, so they can be shown as still being heard. */
    fun pendingStart(text: String, count: Int): Int {
        if (count <= 0) return text.length
        var i = text.length
        var found = 0
        while (i > 0 && found < count) {
            while (i > 0 && text[i - 1].isWhitespace()) i--
            if (i == 0) break
            while (i > 0 && !text[i - 1].isWhitespace()) i--
            found++
        }
        return i
    }

    /**
     * What VerbaFlow's history records and administrators can block: the app's package name (com.whatsapp), with
     * characters a web address cannot hold changed, as VerbaFlow only accepts names shaped like addresses.
     */
    fun siteOf(packageName: String?): String {
        val p = (packageName ?: "").lowercase().replace('_', '-').trim('.', '-')
        return p.ifEmpty { "android" }
    }
}
