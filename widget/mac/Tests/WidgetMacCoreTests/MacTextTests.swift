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
}
