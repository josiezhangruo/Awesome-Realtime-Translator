using System.Text;

namespace AwesomeRealtimeTranslator.Core.Translation;

/// <summary>
/// 术语保护：把指定的英文术语先换成哨兵，翻完再换回中文。
///
/// <b>为什么需要它。</b>macOS 版用的是 Apple 的翻译框架，技术词汇翻得不错。
/// Windows 这边只能自带 OPUS-MT，它是通用语料训练的，在软件面试这个场景里
/// 会把一堆关键词翻错——实测：
///
/// <code>
/// architecture            → 建筑         （应为「架构」）
/// container orchestration → 集装箱的管弦  （应为「容器编排」）
/// </code>
///
/// 面试里这些词恰恰是最不能错的。
///
/// <b>为什么用哨兵。</b>实测过两种办法：
/// <list type="number">
/// <item>把中文术语直接塞进英文原句 → 模型产出乱码（「49/ 18 / 12 / 12 /」），废案。</item>
/// <item>换成 <c>ZQX7</c> 这样的哨兵 → 模型原样复制，位置也对，多个哨兵同时存在也没问题。</item>
/// </list>
///
/// 所以走第二种。纯字符串处理，没有模型依赖，可以完整单测。
/// </summary>
public sealed class TermProtector
{
    /// <summary>
    /// 哨兵前缀。要求：模型会原样复制、不会被切碎、正常面试对话里不可能出现。
    /// 实测 <c>ZQX</c> + 数字满足全部三条。
    /// </summary>
    private const string SentinelPrefix = "ZQX";

    /// <summary>一句话里最多保护几个术语。超过的不保护，保持哨兵为个位数。</summary>
    private const int MaxTerms = 9;

    private readonly List<KeyValuePair<string, string>> _terms;

    public TermProtector(IReadOnlyDictionary<string, string> terms) =>
        // 长的优先匹配，否则 "container orchestration" 会被 "container" 抢先切掉
        _terms = [.. terms.OrderByDescending(t => t.Key.Length)];

    /// <summary>
    /// 软件面试里 OPUS-MT 会翻错、而且错得影响理解的词。
    ///
    /// 刻意保持小而准：每一条都是实测翻错的，不是凭印象堆的。
    /// 纯音译就能看懂的（Kubernetes、Redis、Kafka）不收——模型本来就会原样保留。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultGlossary =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["container orchestration"] = "容器编排",
            ["orchestration"] = "编排",
            ["architecture"] = "架构",
            ["architectural"] = "架构上的",
            ["in production"] = "在生产环境",
            ["production environment"] = "生产环境",
            ["load balancer"] = "负载均衡器",
            ["load balancing"] = "负载均衡",
            ["message queue"] = "消息队列",
            ["service mesh"] = "服务网格",
            ["rate limiting"] = "限流",
            ["sharding"] = "分片",
            ["replication"] = "副本复制",
            ["throughput"] = "吞吐量",
            ["latency"] = "延迟",
            ["scalability"] = "可扩展性",
            ["bottleneck"] = "瓶颈",
            ["deadlock"] = "死锁",
            ["race condition"] = "竞态条件",
            ["garbage collection"] = "垃圾回收",
            ["dependency injection"] = "依赖注入",
            ["code review"] = "代码评审",
            ["pull request"] = "拉取请求",
            ["rollback"] = "回滚",
            ["rollout"] = "灰度发布",
            ["on call"] = "值班",
            ["postmortem"] = "复盘报告",
            ["root cause"] = "根本原因",
            ["trade-off"] = "权衡",
            ["tradeoff"] = "权衡",
            ["edge case"] = "边界情况",
            ["stack trace"] = "调用栈",
            ["cache invalidation"] = "缓存失效",
            ["eventual consistency"] = "最终一致性",
            ["fault tolerance"] = "容错",
            ["observability"] = "可观测性",
        };

    /// <summary>把源文里的术语换成哨兵。返回替换后的文本和还原表。</summary>
    public (string Text, IReadOnlyDictionary<string, string> Restore) Protect(string source)
    {
        if (_terms.Count == 0 || source.Length == 0)
        {
            return (source, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var restore = new Dictionary<string, string>(StringComparer.Ordinal);
        var assigned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string text = source;
        int next = 1;

        foreach ((string term, string replacement) in _terms)
        {
            if (next > MaxTerms)
            {
                break;
            }

            if (!ContainsWord(text, term, out _))
            {
                continue;
            }

            if (!assigned.TryGetValue(term, out string? sentinel))
            {
                sentinel = SentinelPrefix + next.ToString(System.Globalization.CultureInfo.InvariantCulture);
                assigned[term] = sentinel;
                restore[sentinel] = replacement;
                next++;
            }

            text = ReplaceWords(text, term, sentinel);
        }

        return (text, restore);
    }

    /// <summary>把译文里的哨兵换回中文术语。模型丢掉的哨兵不会留下痕迹。</summary>
    public static string Restore(string translated, IReadOnlyDictionary<string, string> restore)
    {
        if (restore.Count == 0 || translated.Length == 0)
        {
            return translated;
        }

        string text = translated;
        foreach ((string sentinel, string replacement) in restore)
        {
            text = text.Replace(sentinel, replacement, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    /// <summary>
    /// 流式译文的中间前缀版 <see cref="Restore"/>。
    ///
    /// 中间前缀可能正好断在哨兵中间——<c>ZQX1</c> 才生成到 <c>ZQ</c>。直接还原的话，
    /// 屏幕上会闪出一个 <c>ZQ</c>，下一帧才变成中文。所以先把结尾那截
    /// <b>还没写完的哨兵</b>砍掉，再按正常规则还原。
    ///
    /// 砍掉的那几个字符下一次报告就会补齐，而贪心解码是 append-only 的，
    /// 所以这不构成「改写」——只是晚一两帧出现。
    /// </summary>
    public static string RestorePartial(string translated, IReadOnlyDictionary<string, string> restore)
    {
        if (restore.Count == 0 || translated.Length == 0)
        {
            return Restore(translated, restore);
        }

        int cut = translated.Length;

        // 结尾的数字可能是哨兵编号的一部分，连同 ZQX 一起往前看
        int i = translated.Length;
        while (i > 0 && char.IsAsciiDigit(translated[i - 1]))
        {
            i--;
        }

        int prefixStart = i - SentinelPrefix.Length;
        if (prefixStart >= 0 &&
            translated.AsSpan(prefixStart, SentinelPrefix.Length)
                .Equals(SentinelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // 完整的哨兵（在 restore 表里）保留，残缺的砍掉
            if (!restore.ContainsKey(translated[prefixStart..]))
            {
                cut = prefixStart;
            }
        }
        else
        {
            // 连 ZQX 都没写全：ZQ / Z 这种结尾
            for (int len = SentinelPrefix.Length - 1; len > 0; len--)
            {
                if (translated.Length >= len &&
                    translated.AsSpan(translated.Length - len)
                        .Equals(SentinelPrefix.AsSpan(0, len), StringComparison.OrdinalIgnoreCase))
                {
                    cut = translated.Length - len;
                    break;
                }
            }
        }

        return Restore(translated[..cut], restore);
    }

    /// <summary>按词边界找术语。避免 "on call" 命中 "falcon called"。</summary>
    private static bool ContainsWord(string haystack, string needle, out int index)
    {
        index = -1;
        int from = 0;

        while (from <= haystack.Length - needle.Length)
        {
            int at = haystack.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }

            if (IsWordBoundary(haystack, at, needle.Length))
            {
                index = at;
                return true;
            }

            from = at + 1;
        }

        return false;
    }

    private static bool IsWordBoundary(string text, int start, int length)
    {
        bool leftOk = start == 0 || !IsWordChar(text[start - 1]);
        int end = start + length;
        bool rightOk = end >= text.Length || !IsWordChar(text[end]);
        return leftOk && rightOk;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string ReplaceWords(string text, string needle, string replacement)
    {
        var sb = new StringBuilder(text.Length);
        int from = 0;

        while (from < text.Length)
        {
            int at = text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                sb.Append(text, from, text.Length - from);
                break;
            }

            if (!IsWordBoundary(text, at, needle.Length))
            {
                sb.Append(text, from, at - from + 1);
                from = at + 1;
                continue;
            }

            sb.Append(text, from, at - from);
            sb.Append(replacement);
            from = at + needle.Length;
        }

        return sb.ToString();
    }
}
