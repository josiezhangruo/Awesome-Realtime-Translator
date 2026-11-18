import Foundation

/// 判断一句定稿的话是不是「面试官在提问」。
///
/// 纯逻辑、无依赖，可完整单测（见 QuestionDetectorTests）。
///
/// 为什么不能只看问号：识别模型的标点是推断出来的，口语升调的疑问句
/// （"You worked on the search team"）经常拿不到 `?`；而被静音或超长硬切出来的
/// 句子根本没有句末标点。所以分三档判断，**有标点就信标点**，没标点才看句首。
enum QuestionDetector {

    static func isQuestion(_ text: String) -> Bool {
        let trimmed = Self.stripTrailingBrackets(text.trimmed)
        guard let last = trimmed.last else { return false }

        // 1. 明确的问号：最可靠，直接判定
        if last == "?" || last == "？" { return true }

        // 2. 中文语气词结尾：「你做过吗」「那你怎么看呢」
        if "吗呢吧".contains(last) { return true }

        // 3. 面试祈使句，**先于标点判断**。
        //
        //    这一档的位置是实测改出来的。原来它排在标点否决之后，结果
        //    "Tell me about a time you disagreed with your manager." 被句号否决掉，
        //    整个功能最该钉住的那类问题一个都钉不上 —— 而祈使句本来就该带句号，
        //    拿句号否决它等于否决了这一整类。
        //
        //    靠词组而不是单词来防误判："tell me" 是提问，"tell you what" 不是。
        if isImperativePrompt(trimmed) { return true }

        // 4. 模型给了陈述句标点，就相信它。
        //    "Did that help you?" 已经被第 1 档收走，剩下以句号结尾的
        //    "Did" 开头句（"Did I mention we're hiring."）不该被当成提问。
        if ".!。！…;；".contains(last) { return false }

        // 5. 没有任何句末标点 —— 被静音或长度硬切出来的句子。只能看句首。
        return openers.contains(firstWord(of: trimmed))
    }

    /// 面试里等价于提问的祈使句。
    ///
    /// "Tell me about a time you disagreed with your manager" 语法上不是疑问句，
    /// 还经常带句号，但它就是要被钉住的那个问题 —— 这才是这个功能存在的理由。
    private static func isImperativePrompt(_ text: String) -> Bool {
        let lowered = text.lowercased()
        // 中文没有词边界，只能前缀匹配
        for prefix in ["说说", "聊聊", "请问", "谈谈", "讲讲"] where lowered.hasPrefix(prefix) {
            return true
        }
        let words = lowered
            .split(whereSeparator: { $0 == " " || $0 == "\n" })
            .prefix(2)
            .map { $0.trimmingCharacters(in: CharacterSet(charactersIn: "(（\"'“‘,，、")) }
        guard let first = words.first else { return false }
        // 单词就能确定的：这些词开头几乎只可能是在提要求
        if ["describe", "explain", "share", "suppose", "imagine"].contains(first) {
            return true
        }
        // 需要看第二个词：tell me ✓ / tell you what ✗
        guard words.count == 2 else { return false }
        return ["tell", "walk", "give", "show"].contains(first)
            && ["me", "us"].contains(words[1])
    }

    /// 句首触发词。
    ///
    /// 除了疑问词和助动词，还收了 tell / walk / describe 这类**祈使句**：
    /// "Tell me about a time you disagreed with your manager" 语法上不是疑问句，
    /// 但它就是面试里那个要被钉住的问题，这才是这个功能存在的理由。
    private static let openers: Set<String> = [
        // 疑问词
        "what", "why", "how", "when", "where", "which", "who", "whom", "whose",
        // 助动词 / 系动词开头的一般疑问句
        "can", "could", "would", "will", "do", "does", "did", "are", "is",
        "was", "were", "have", "has", "had", "should", "shall", "may", "might",
        // 面试里等价于提问的祈使句
        "tell", "walk", "describe", "explain", "share", "suppose", "imagine",
        // 中文
        "什么", "为什么", "怎么", "怎样", "如何", "能否", "可否", "请问", "说说", "聊聊",
    ]

    /// 取句首第一个词，小写、去掉包裹的标点。中文没有词边界，取前两个字做前缀匹配。
    private static func firstWord(of text: String) -> String {
        let head = text.split(whereSeparator: { $0 == " " || $0 == "\n" }).first.map(String.init) ?? ""
        let cleaned = head
            .trimmingCharacters(in: CharacterSet(charactersIn: "(（\"'“‘,，、"))
            .lowercased()
        if cleaned.isEmpty { return "" }
        // 中文：openers 里存的是 2~3 字的词，拿句首最多 3 个字去比
        if let first = cleaned.first, isCJK(first) {
            for length in stride(from: min(3, cleaned.count), through: 1, by: -1) {
                let candidate = String(cleaned.prefix(length))
                if openers.contains(candidate) { return candidate }
            }
            return String(cleaned.prefix(2))
        }
        return cleaned
    }

    /// 去掉句尾的引号和括号，让 `He asked, "why?"` 这种也能看到真正的句末标点。
    private static func stripTrailingBrackets(_ text: String) -> String {
        var result = Substring(text)
        while let last = result.last, "\"'”’)）》」".contains(last) {
            result = result.dropLast()
        }
        return String(result)
    }

    private static func isCJK(_ c: Character) -> Bool {
        guard let s = c.unicodeScalars.first else { return false }
        return (0x3000...0x9FFF).contains(s.value) || (0xF900...0xFAFF).contains(s.value)
    }
}
