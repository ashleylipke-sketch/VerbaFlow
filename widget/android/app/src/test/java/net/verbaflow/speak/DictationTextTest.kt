package net.verbaflow.speak

import org.junit.Assert.assertEquals
import org.junit.Test

class DictationTextTest {
    @Test fun passagesJoinIntoParagraphs() {
        val s = listOf(LivePara("Dear Ann,", true, null), LivePara("thank you.", false, null), LivePara("Regards", true, null))
        assertEquals(listOf("Dear Ann, thank you.", "Regards"), DictationText.paragraphs(s))
    }

    @Test fun listMarkersAreCountedAfresh() {
        val s = listOf(
            LivePara("Eggs", true, "number"), LivePara("Milk", true, "number"),
            LivePara("Then", true, null), LivePara("Bread", true, "number"), LivePara("Jam", true, "bullet"),
        )
        assertEquals(listOf("1. Eggs", "2. Milk", "Then", "1. Bread", "• Jam"), DictationText.paragraphs(s))
    }

    @Test fun lineBreaksInsideAPassageStartParagraphs() {
        assertEquals(listOf("One", "Two"), DictationText.paragraphs(listOf(LivePara("One\r\nTwo", null, null))))
    }

    @Test fun heardKeepsALeadingBreak() {
        assertEquals("\n\nHello", DictationText.heard(listOf(LivePara("Hello", true, null)), true))
        assertEquals("", DictationText.heard(emptyList(), true))
    }

    @Test fun joinAddsASpaceUnlessPunctuation() {
        assertEquals("Hello there", DictationText.join("Hello", "there"))
        assertEquals("Hello, there", DictationText.join("Hello", ", there"))
        assertEquals("there", DictationText.join("  ", "there"))
        assertEquals("Hello", DictationText.join("Hello ", ""))
    }

    @Test fun spacedAsAPersonTyping() {
        assertEquals(" word", DictationText.spaced("a", "word"))
        assertEquals("word", DictationText.spaced(" ", "word"))
        assertEquals("word", DictationText.spaced(null, "word"))
        assertEquals(".", DictationText.spaced("a", "."))
    }

    @Test fun pendingWordsStartAtTheRightPlace() {
        val t = "one two  three"
        assertEquals(t.indexOf("three"), DictationText.pendingStart(t, 1))
        assertEquals(t.indexOf("two"), DictationText.pendingStart(t, 2))
        assertEquals(0, DictationText.pendingStart(t, 9))
        assertEquals(t.length, DictationText.pendingStart(t, 0))
    }

    @Test fun wordsAndSites() {
        assertEquals(3, DictationText.words(" a b\n\nc "))
        assertEquals("com.whatsapp", DictationText.siteOf("com.WhatsApp"))
        assertEquals("com.my-app", DictationText.siteOf("com.my_app"))
        assertEquals("android", DictationText.siteOf(null))
    }
}
