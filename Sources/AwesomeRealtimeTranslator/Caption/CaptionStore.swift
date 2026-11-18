import Foundation
import Observation

struct CaptionLine: Identifiable, Sendable {
    let id: UUID
    let source: String
    var translation: String?
    var translationFailed: Bool = false
    let startedAt: TimeInterval
    let wallClock: Date

    var isTranslating: Bool { translation == nil && !translationFailed }
}

/// 字幕状态。单一数据源，悬浮窗和历史记录都从这里读。
@MainActor
@Observable
final class CaptionStore {
    /// 已定稿的句子，按时间升序
    private(set) var lines: [CaptionLine] = []
    /// 未定稿的尾句，只显示在原文区末尾，永不翻译
    var pendingSource: String = ""
    /// 给用户看的一句话状态（权限、下载、降级提示）
    var statusMessage: String?
    var sessionStartedAt: Date?
    /// 悬浮窗要显示运行状态，放在同一个 Observable 里，省掉一层状态同步
    var isRunning: Bool = false

    private let maxLines = 2000

    // MARK: - 钉住当前问题

    /// 当前被钉住的提问，按说出顺序。存 id 而不是文本，这样译文回来时钉住条会自己更新。
    private(set) var pinnedQuestionIDs: [UUID] = []
    /// 最后一段被钉住的话结束的时间，用来判断下一句是不是同一个问题的后半截
    private var lastPinnedEndedAt: TimeInterval?

    /// 追问窗口：提问结束后这么多秒内到达的句子，算同一个问题的后半截。
    ///
    /// 这个判断之所以成立，是因为**本应用不采集麦克风**：这段时间里能被听到的
    /// 只可能是面试官继续说，不可能是你开口回答。
    private static let continuationWindow: TimeInterval = 2.5
    /// 一个问题最多钉三段，再多就不是「别忘了问题」而是挡住字幕了
    private static let maxPinnedSegments = 3

    /// 钉住条要显示的内容。句子可能已被 maxLines 挤掉，所以用 compactMap。
    var pinnedQuestion: [CaptionLine] {
        pinnedQuestionIDs.compactMap { id in lines.first { $0.id == id } }
    }

    func startSession() {
        lines.removeAll()
        pendingSource = ""
        pinnedQuestionIDs.removeAll()
        lastPinnedEndedAt = nil
        sessionStartedAt = Date()
    }

    func append(_ sentence: Sentence) {
        lines.append(CaptionLine(
            id: sentence.id,
            source: sentence.text,
            translation: nil,
            startedAt: sentence.startedAt,
            wallClock: Date()
        ))
        if lines.count > maxLines { lines.removeFirst(lines.count - maxLines) }
        updatePin(with: sentence)
    }

    /// 钉住最近一个提问。不设过期时间：你答了五分钟，刚才那个问题仍然是「当前问题」，
    /// 直到下一个问题出现才该被换掉。
    ///
    /// **先判追问，再判新问题** —— 顺序反了就会出现实测里的那个 bug：
    /// "Tell me about X." / "And how would you handle it differently now?"
    /// 后半句自带问号，被当成新问题把前半句整个替换掉，钉住条最后只剩后半截，
    /// 恰好和这个功能的目的相反。紧挨着的两句本来就是同一个问题。
    private func updatePin(with sentence: Sentence) {
        if isContinuation(of: sentence) {
            pinnedQuestionIDs.append(sentence.id)
            lastPinnedEndedAt = sentence.endedAt
            return
        }
        guard QuestionDetector.isQuestion(sentence.text) else { return }
        pinnedQuestionIDs = [sentence.id]
        lastPinnedEndedAt = sentence.endedAt
    }

    /// 这一句是不是上一个提问的后半截。
    ///
    /// 判据是时间：提问结束 `continuationWindow` 秒内到达。之所以够用，是因为
    /// 本应用**不采集麦克风** —— 这段时间里能被听到的只可能是面试官继续说，
    /// 不可能是你开口回答。所以不看内容，自带问号的后半句也算追问。
    private func isContinuation(of sentence: Sentence) -> Bool {
        guard !pinnedQuestionIDs.isEmpty,
              pinnedQuestionIDs.count < Self.maxPinnedSegments,
              let end = lastPinnedEndedAt
        else { return false }
        return sentence.startedAt - end <= Self.continuationWindow
    }

    func setTranslation(_ text: String, for id: UUID) {
        guard let index = lines.firstIndex(where: { $0.id == id }) else { return }
        lines[index].translation = text
        lines[index].translationFailed = false
    }

    func markTranslationFailed(for id: UUID) {
        guard let index = lines.firstIndex(where: { $0.id == id }) else { return }
        lines[index].translationFailed = true
    }

    /// 最近 n 条，供悬浮窗显示
    func tail(_ n: Int) -> [CaptionLine] {
        Array(lines.suffix(max(1, n)))
    }

    /// 最近几句原文，给需要上下文的翻译引擎用
    func recentContext(_ n: Int) -> [String] {
        Array(lines.suffix(n).map(\.source))
    }

    // MARK: - 导出（F10 的数据层）

    var plainText: String {
        lines.map { line in
            let stamp = Self.stamp(line.startedAt)
            let translation = line.translation.map { "\n\($0)" } ?? ""
            return "[\(stamp)] \(line.source)\(translation)"
        }.joined(separator: "\n\n")
    }

    var markdown: String {
        let header: String
        if let sessionStartedAt {
            let formatter = DateFormatter()
            formatter.dateFormat = "yyyy-MM-dd HH:mm"
            header = "# 面试记录 \(formatter.string(from: sessionStartedAt))\n\n"
        } else {
            header = "# 面试记录\n\n"
        }
        let body = lines.map { line in
            var block = "**[\(Self.stamp(line.startedAt))]** \(line.source)"
            if let translation = line.translation, !translation.isEmpty {
                block += "\n\n> \(translation)"
            }
            return block
        }.joined(separator: "\n\n")
        return header + body + "\n"
    }

    private static func stamp(_ seconds: TimeInterval) -> String {
        let total = Int(seconds.rounded())
        return String(format: "%02d:%02d", total / 60, total % 60)
    }
}
