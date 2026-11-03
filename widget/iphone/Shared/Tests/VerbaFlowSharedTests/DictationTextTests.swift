import XCTest
@testable import VerbaFlowShared

final class DictationTextTests: XCTestCase {
    func testPassagesJoinIntoParagraphsWithListMarkers() {
        let segs = [
            LivePara(text: "Dear Sam,"),
            LivePara(text: "thank you", newParagraph: false),
            LivePara(text: "First thing", newParagraph: true, list: "number"),
            LivePara(text: "Second thing", newParagraph: true, list: "number"),
            LivePara(text: "Eggs", newParagraph: true, list: "bullet"),
        ]
        XCTAssertEqual(DictationText.paragraphs(segs), ["Dear Sam, thank you", "1. First thing", "2. Second thing", "• Eggs"])
        XCTAssertEqual(DictationText.heard(segs, startsWithBreak: false), "Dear Sam, thank you\n\n1. First thing\n\n2. Second thing\n\n• Eggs")
    }

    func testALineBreakInsideAPassageStartsAParagraph() {
        XCTAssertEqual(DictationText.paragraphs([LivePara(text: "One\nTwo")]), ["One", "Two"])
    }

    func testNewParagraphSaidFirstBreaksAfterTheWordsBefore() {
        XCTAssertEqual(DictationText.heard([LivePara(text: "Next")], startsWithBreak: true), "\n\nNext")
    }

    func testSpeakMoreJoinsWithASpaceUnlessPunctuationLeads() {
        XCTAssertEqual(DictationText.join("Hello there", "how are you"), "Hello there how are you")
        XCTAssertEqual(DictationText.join("Hello there", ", Sam"), "Hello there, Sam")
        XCTAssertEqual(DictationText.join("", "Hi"), "Hi")
        XCTAssertEqual(DictationText.join("Hi ", ""), "Hi")
    }

    func testASpaceIsAddedOnlyWhereAPersonWouldTypeOne() {
        XCTAssertEqual(DictationText.spaced(before: "Hello", "world"), " world")
        XCTAssertEqual(DictationText.spaced(before: "Hello ", "world"), "world")
        XCTAssertEqual(DictationText.spaced(before: nil, "world"), "world")
        XCTAssertEqual(DictationText.spaced(before: "Hello", ", world"), ", world")
        XCTAssertEqual(DictationText.words(" one  two\nthree "), 3)
    }

    func testTheKeyboardsNextRequestNumberIsAlwaysHigher() {
        var s = AppStatus(); s.handled = 4
        XCTAssertEqual(Link.nextNumber(status: s, lastSent: KeyboardRequest(no: 7, kind: .stop)), 8)
        XCTAssertEqual(Link.nextNumber(status: s, lastSent: nil), 5)
        XCTAssertEqual(Link.nextNumber(status: nil, lastSent: nil), 1)
    }

    func testStatusAndRequestsSurviveTheSharedStorage() throws {
        let store = try XCTUnwrap(UserDefaults(suiteName: "verbaflow-tests-\(UUID().uuidString)"))
        var s = AppStatus(); s.phase = .checking; s.text = "Hello"; s.alive = Date()
        Link.write(s, Link.statusKey, notify: Link.statusChanged, in: store)
        let back = try XCTUnwrap(Link.read(AppStatus.self, Link.statusKey, in: store))
        XCTAssertEqual(back.phase, .checking)
        XCTAssertEqual(back.text, "Hello")
        XCTAssertTrue(back.isAlive())
        XCTAssertFalse(AppStatus().isAlive())
    }
}
