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

    // MARK: - 钉住当前问题 / 问题备忘录

    /// 本场所有提问，按「轮」分组。一轮 = 面试官一口气问的那几句。
    /// 钉住条只显示最后一组，备忘录显示全部。
    /// 存 id 而不是文本，这样译文回来时两处都会自己更新。
    private(set) var questionGroups: [[UUID]] = []
    /// 当前这一轮最后一句话结束的时间，用来判断下一句是否还属于这一轮
    private var lastPinnedEndedAt: TimeInterval?

    /// 同一轮提问的间隔窗口：上一句结束后这么多秒内到达的句子，算同一轮。
    ///
    /// 这个判断之所以成立，是因为**本应用不采集麦克风**：这段时间里能被听到的
    /// 只可能是面试官继续说，不可能是你开口回答。所以不看内容，自带问号的
    /// 后半句也算同一轮。
    ///
    /// 定成 5 秒而不是更短：真人提问之间会停顿、会「嗯」一下、会翻简历，
    /// 「说说你最近的项目……另外你怎么处理和上级的分歧」中间隔三四秒很正常。
    /// 窗口太短就会把一轮里的后几个问题各自当成新问题，把前面的替换掉。
    private static let continuationWindow: TimeInterval = 5
    /// 一轮最多钉三段，再多就不是「别忘了问题」而是挡住字幕了。
    /// 超出的句子不再显示，但**仍然算这一轮**（见 updatePin）。
    private static let maxPinnedSegments = 3

    /// 钉住条要显示的内容 —— 最后一轮提问。
    var pinnedQuestion: [CaptionLine] {
        captionLines(for: questionGroups.last ?? [])
    }

    /// 备忘录：本场每一轮提问，按时间升序。
    /// 整组的句子都被 maxLines 挤掉之后会变成空数组，过滤掉。
    var questionMemo: [[CaptionLine]] {
        questionGroups.map(captionLines(for:)).filter { !$0.isEmpty }
    }

    /// 句子可能已被 maxLines 挤掉，所以用 compactMap。
    private func captionLines(for ids: [UUID]) -> [CaptionLine] {
        ids.compactMap { id in lines.first { $0.id == id } }
    }

    func startSession() {
        lines.removeAll()
        pendingSource = ""
        questionGroups.removeAll()
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

    /// 钉住最近一轮提问。不设过期时间：你答了五分钟，刚才那个问题仍然是「当前问题」，
    /// 直到下一轮提问出现才该被换掉。
    ///
    /// **先判同一轮，再判新问题** —— 顺序反了就会出现实测里的那个 bug：
    /// "Tell me about X." / "And how would you handle it differently now?"
    /// 后半句自带问号，被当成新问题把前半句整个替换掉，钉住条最后只剩后半截，
    /// 恰好和这个功能的目的相反。紧挨着的两句本来就是同一轮。
    private func updatePin(with sentence: Sentence) {
        if isSameRound(as: sentence) {
            // 满了就不再显示这一句，但时间窗**照样推进** —— 否则一轮里的第 4 句
            // 会掉到下面的分支，被当成新问题另起一组，把前三句整个挤走。
            if let last = questionGroups.indices.last,
               questionGroups[last].count < Self.maxPinnedSegments {
                questionGroups[last].append(sentence.id)
            }
            lastPinnedEndedAt = sentence.endedAt
            return
        }
        guard QuestionDetector.isQuestion(sentence.text) else { return }
        questionGroups.append([sentence.id])
        lastPinnedEndedAt = sentence.endedAt
    }

    /// 这一句是否还属于当前这一轮提问。判据只有时间，不看内容。
    private func isSameRound(as sentence: Sentence) -> Bool {
        guard !questionGroups.isEmpty, let end = lastPinnedEndedAt else { return false }
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
