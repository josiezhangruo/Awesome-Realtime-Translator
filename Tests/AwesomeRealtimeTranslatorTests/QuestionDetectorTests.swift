import XCTest

final class QuestionDetectorTests: XCTestCase {

    // MARK: - 第 1 档：问号

    func testQuestionMark() {
        XCTAssertTrue(QuestionDetector.isQuestion("What was the root cause?"))
        XCTAssertTrue(QuestionDetector.isQuestion("那你当时怎么定位的？"))
    }

    func testQuestionMarkInsideClosingQuote() {
        // 识别模型会把引号带出来，剥掉之后才看得到真正的句末标点
        XCTAssertTrue(QuestionDetector.isQuestion("He asked, \"why did it fail?\""))
    }

    // MARK: - 第 2 档：中文语气词

    func testChineseModalParticles() {
        XCTAssertTrue(QuestionDetector.isQuestion("你做过分布式系统吗"))
        XCTAssertTrue(QuestionDetector.isQuestion("那你怎么看呢"))
    }

    // MARK: - 第 3 档：陈述句标点一律不是提问

    func testStatementPunctuationWins() {
        XCTAssertFalse(QuestionDetector.isQuestion("I was responsible for the search pipeline."))
        XCTAssertFalse(QuestionDetector.isQuestion("我们当时用的是 Kafka。"))
    }

    /// 这条是防误判的核心：句首是触发词，但模型已经判成陈述句，就相信标点。
    func testTriggerWordWithStatementPunctuationIsNotAQuestion() {
        XCTAssertFalse(QuestionDetector.isQuestion("Did I mention we're hiring for two roles."))
        XCTAssertFalse(QuestionDetector.isQuestion("Tell you what, let's move on."))
    }

    // MARK: - 第 4 档：没有句末标点，看句首

    /// 被静音或长度硬切出来的句子没有任何句末标点，这是实际最常见的情况。
    func testOpenerWithoutPunctuation() {
        XCTAssertTrue(QuestionDetector.isQuestion("How did you find the bottleneck"))
        XCTAssertTrue(QuestionDetector.isQuestion("Can you walk me through the architecture"))
    }

    /// 祈使句是面试里真正的「问题」，语法上却不是疑问句——这是收 tell/walk 的理由。
    func testImperativeInterviewPrompts() {
        XCTAssertTrue(QuestionDetector.isQuestion("Tell me about a time you disagreed with your manager"))
        XCTAssertTrue(QuestionDetector.isQuestion("Walk me through your last incident"))
        XCTAssertTrue(QuestionDetector.isQuestion("Describe the hardest bug you've shipped"))
    }

    func testPlainStatementWithoutPunctuationIsNotAQuestion() {
        XCTAssertFalse(QuestionDetector.isQuestion("We ran about three hundred nodes in production"))
        XCTAssertFalse(QuestionDetector.isQuestion("Yeah that makes sense"))
    }

    func testChineseOpenersWithoutPunctuation() {
        XCTAssertTrue(QuestionDetector.isQuestion("为什么选择这个方案"))
        XCTAssertTrue(QuestionDetector.isQuestion("说说你最近做的项目"))
        XCTAssertFalse(QuestionDetector.isQuestion("我们团队当时有十个人"))
    }

    // MARK: - 边界

    func testEmptyAndWhitespace() {
        XCTAssertFalse(QuestionDetector.isQuestion(""))
        XCTAssertFalse(QuestionDetector.isQuestion("   \n "))
    }

    func testLeadingQuoteDoesNotHideTheOpener() {
        XCTAssertTrue(QuestionDetector.isQuestion("\"How would you scale it"))
    }

    func testCaseInsensitive() {
        XCTAssertTrue(QuestionDetector.isQuestion("WHAT WENT WRONG"))
    }
}
