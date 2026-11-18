import XCTest
@testable import WidgetMacCore

final class MacTextTests: XCTestCase {
    func testParagraphsGetAnEmptyLineByDefault() {
        XCTAssertEqual(MacText.forPasting("One.\n\nTwo.", emptyLineBetweenParagraphs: true), "One.\n\nTwo.")
        XCTAssertEqual(MacText.forPasting("One.\n\nTwo.", emptyLineBetweenParagraphs: false), "One.\nTwo.")
    }

    func testANewParagraphSaidFirstBreaksAfterWhatIsThere() {
        XCTAssertEqual(MacText.forPasting("\n\nHello.", emptyLineBetweenParagraphs: true), "\n\nHello.")
        XCTAssertEqual(MacText.forPasting("\n\nHello.", emptyLineBetweenParagraphs: false), "\nHello.")
    }

    func testTrailingSpaceIsDropped() {
        XCTAssertEqual(MacText.forPasting("Hello.  \n\n", emptyLineBetweenParagraphs: true), "Hello.")
    }

    func testWordAndPagesSpaceParagraphsThemselves() {
        XCTAssertFalse(MacText.emptyLine(.automatic, program: "com.microsoft.Word"))
        XCTAssertTrue(MacText.emptyLine(.automatic, program: "com.microsoft.Outlook"))
        XCTAssertTrue(MacText.emptyLine(.emptyLine, program: "com.microsoft.Word"))
        XCTAssertFalse(MacText.emptyLine(.newLineOnly, program: "com.apple.mail"))
    }

    func testSiteIsTheBundleIdInLowerCase() {
        XCTAssertEqual(MacText.site(bundleId: "com.microsoft.Outlook"), "com.microsoft.outlook")
        XCTAssertEqual(MacText.site(bundleId: nil), "unknown.mac")
        XCTAssertEqual(MacText.site(bundleId: "net.whatsapp.WhatsApp"), "net.whatsapp.whatsapp")
    }

    func testServerAddress() {
        XCTAssertEqual(MacText.cleanServer("localhost:5044/")?.absoluteString, "https://localhost:5044")
        XCTAssertEqual(MacText.cleanServer("http://localhost:5044")?.absoluteString, "http://localhost:5044")
        XCTAssertNil(MacText.cleanServer("ftp://example.com"))
        XCTAssertNil(MacText.cleanServer(""))
    }

    func testSettingsAreKept() {
        let d = UserDefaults(suiteName: "verbaflow-test-\(UUID().uuidString)")!
        XCTAssertFalse(WidgetSettings.exists(d))
        var s = WidgetSettings()
        s.languages = "af-ZA"; s.gap = .newLineOnly
        s.save(d)
        XCTAssertTrue(WidgetSettings.exists(d))
        XCTAssertEqual(WidgetSettings.load(d), s)
    }

    func testOlderSettingsWithoutTheNewKeysStillLoad() {
        let d = UserDefaults(suiteName: "verbaflow-test-\(UUID().uuidString)")!
        d.set(Data(#"{"server":"https://vf.example.com","languages":"af-ZA","gap":"newLineOnly","leaveBrowsersToExtension":false,"testUser":"bob"}"#.utf8), forKey: "settings")
        let s = WidgetSettings.load(d)
        XCTAssertEqual(s.languages, "af-ZA")
        XCTAssertEqual(s.keys, MacShortcut.standard)
        XCTAssertTrue(s.insertMethods.isEmpty)
    }

    func testProgramsGetTheUsualWayUnlessKnownOrChosen() {
        var s = WidgetSettings()
        XCTAssertEqual(s.method(for: "com.apple.mail"), .paste)
        XCTAssertEqual(s.method(for: "com.microsoft.rdc.macos"), .type)
        s = s.with(.copy, for: "com.apple.Mail")
        XCTAssertEqual(s.method(for: "com.apple.mail"), .copy)
        s = s.with(.paste, for: "com.apple.mail")
        XCTAssertTrue(s.insertMethods.isEmpty)
        let d = UserDefaults(suiteName: "verbaflow-test-\(UUID().uuidString)")!
        s = s.with(.type, for: "com.example.app")
        s.shortcut = "Control+Shift+F9"
        s.save(d)
        XCTAssertEqual(WidgetSettings.load(d), s)
        XCTAssertEqual(WidgetSettings.load(d).keys.symbols, "⌃⇧F9")
    }

    func testShortcutsNeedControlOptionOrMoreThanCommand() {
        XCTAssertEqual(MacShortcut.standard.text, "Option+Shift+D")
        XCTAssertEqual(MacShortcut.standard.symbols, "⌥⇧D")
        XCTAssertNil(MacShortcut.parse("Shift+D"))
        XCTAssertNil(MacShortcut.parse("Command+D"))
        XCTAssertNotNil(MacShortcut.parse("Command+Shift+D"))
        XCTAssertNil(MacShortcut.parse("Option+D+E"))
        XCTAssertNil(MacShortcut.parse("Option+Home"))
        for h in MacShortcut.holds { XCTAssertEqual(MacShortcut.from(hold: h, key: "D")?.hold, h) }
    }

    func testTypingSplitsLinesAndLongText() {
        XCTAssertEqual(TypedKey.keys("ab\r\ncé"), [.characters("ab"), .enter, .characters("cé")])
        XCTAssertEqual(TypedKey.keys(String(repeating: "x", count: 20)), [.characters(String(repeating: "x", count: 16)), .characters("xxxx")])
    }
}

final class VoiceCommandTests: XCTestCase {
    func testCommandsNeedTheWakeWordExceptWhileChecking() {
        XCTAssertEqual(VoiceCommands.parse("VerbaFlow, dictate.", checking: false), .dictate)
        XCTAssertEqual(VoiceCommands.parse("Verba flow start dictating", checking: false), .dictate)
        XCTAssertNil(VoiceCommands.parse("dictate", checking: false))
        XCTAssertEqual(VoiceCommands.parse("Accept.", checking: true), .accept)
        XCTAssertNil(VoiceCommands.parse("VerbaFlow open Word", checking: true))
    }

    func testProgramsKeysAndCells() {
        XCTAssertEqual(VoiceCommands.parse("VerbaFlow, switch to Outlook.", checking: false), .switchTo("outlook"))
        XCTAssertEqual(VoiceCommands.parse("VerbaFlow open Excel", checking: false), .open("excel"))
        XCTAssertEqual(VoiceCommands.parse("VerbaFlow, next cell", checking: false), .key(.nextCell))
        XCTAssertEqual(VoiceCommands.parse("VerbaFlow go to cell B5", checking: false), .goToCell("B5"))
        XCTAssertEqual(VoiceCommands.parse("verba flow go to see one hundred and five", checking: false), .goToCell("C105"))
        XCTAssertEqual(VoiceCommands.parse("verba flow go to word", checking: false), .switchTo("word"))
    }

    func testTheNewestCommandInARunningTranscript() {
        XCTAssertEqual(VoiceCommands.latest(in: "so I think we should VerbaFlow switch to Mail", checking: false), .switchTo("mail"))
        XCTAssertNil(VoiceCommands.latest(in: "we could switch to Mail", checking: false))
        XCTAssertEqual(VoiceCommands.latest(in: "hmm looks fine accept", checking: true), .accept)
    }

    func testStopDictatingFinishes() {
        XCTAssertEqual(VoiceCommands.withoutStop("Dear Ann, thanks. Stop dictating."), "Dear Ann, thanks.")
        XCTAssertEqual(VoiceCommands.withoutStop("Dear Ann, stop dictating"), "Dear Ann")
        XCTAssertNil(VoiceCommands.withoutStop("Please stop dictating to me and write."))
    }

    func testProgramNames() {
        XCTAssertEqual(ProgramNames.clean("Microsoft Teams (work or school)"), "teams")
        let apps = [(name: "word", item: "w"), (name: "excel", item: "x"), (name: "visual studio code", item: "v")]
        XCTAssertEqual(ProgramNames.best("Excel", apps), "x")
        XCTAssertEqual(ProgramNames.best("visual studio", apps), "v")
        XCTAssertNil(ProgramNames.best("keynote", apps))
        XCTAssertFalse(MacText.emptyLine(.automatic, program: "com.microsoft.Excel"))
    }
}
