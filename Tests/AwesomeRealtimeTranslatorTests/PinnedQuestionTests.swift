import XCTest

/// 钉住条的行为。两条都是实测 `say -v Samantha "Tell me about a time you disagreed
/// with your manager. And how would you handle it differently now?"` 之后补的回归测试。
@MainActor
final class PinnedQuestionTests: XCTestCase {

    private func store() -> CaptionStore {
        let s = CaptionStore()
        s.startSession()
        return s
    }

    private func sentence(_ text: String, from: TimeInterval, to: TimeInterval) -> Sentence {
        Sentence(text: text, startedAt: from, endedAt: to)
    }

    // MARK: - bug 1：祈使句带句号

    /// 原来第 3 档「有句号就不是提问」排在祈使句之前，把这类问题全否决了。
    /// 而它恰恰是这个功能最该钉住的那一类。
    func testImperativePromptWithPeriodIsPinned() {
        let s = store()
        s.append(sentence("Tell me about a time you disagreed with your manager.", from: 0, to: 4))
        XCTAssertEqual(s.pinnedQuestion.map(\.source),
                       ["Tell me about a time you disagreed with your manager."])
    }

    // MARK: - bug 2：后半句把前半句替换掉

    /// 两段式问题：后半句自带问号，原来被判成「新问题」整个替换掉前半句，
    /// 钉住条最后只剩后半截 —— 恰好和这个功能的目的相反。
    func testSecondHalfOfAQuestionIsAppendedNotReplaced() {
        let s = store()
        s.append(sentence("Tell me about a time you disagreed with your manager.", from: 0, to: 4))
        s.append(sentence("And how would you handle it differently now?", from: 4.3, to: 7))
        XCTAssertEqual(s.pinnedQuestion.count, 2)
        XCTAssertTrue(s.pinnedQuestion[0].source.hasPrefix("Tell me"))
        XCTAssertTrue(s.pinnedQuestion[1].source.hasPrefix("And how"))
    }

    /// 隔得够远的新问题仍然要替换，否则钉住条会一直往后长。
    func testLaterQuestionReplacesThePin() {
        let s = store()
        s.append(sentence("Tell me about your last incident.", from: 0, to: 4))
        s.append(sentence("I see.", from: 60, to: 61))
        s.append(sentence("How did you scale the write path?", from: 120, to: 124))
        XCTAssertEqual(s.pinnedQuestion.map(\.source), ["How did you scale the write path?"])
    }

    // MARK: - 边界

    func testPlainStatementsNeverPin() {
        let s = store()
        s.append(sentence("We ran about three hundred nodes in production.", from: 0, to: 4))
        XCTAssertTrue(s.pinnedQuestion.isEmpty)
    }

    /// 最多钉三段，再多就不是「别忘了问题」而是挡住字幕了。
    func testPinIsCappedAtThreeSegments() {
        let s = store()
        s.append(sentence("Tell me about your last project.", from: 0, to: 2))
        s.append(sentence("Say the hardest part.", from: 2.2, to: 4))
        s.append(sentence("And what you'd change.", from: 4.2, to: 6))
        s.append(sentence("Also the timeline.", from: 6.2, to: 8))
        XCTAssertEqual(s.pinnedQuestion.count, 3)
    }

    func testStartSessionClearsThePin() {
        let s = store()
        s.append(sentence("How did you find the bottleneck?", from: 0, to: 3))
        XCTAssertFalse(s.pinnedQuestion.isEmpty)
        s.startSession()
        XCTAssertTrue(s.pinnedQuestion.isEmpty)
    }

    /// 钉的是句子 id 不是文本，所以译文回来时钉住条自己就更新了。
    func testPinPicksUpTheTranslationWhenItArrives() {
        let s = store()
        let q = sentence("How did you find the bottleneck?", from: 0, to: 3)
        s.append(q)
        XCTAssertNil(s.pinnedQuestion.first?.translation)
        s.setTranslation("你是怎么定位到瓶颈的？", for: q.id)
        XCTAssertEqual(s.pinnedQuestion.first?.translation, "你是怎么定位到瓶颈的？")
    }
}
