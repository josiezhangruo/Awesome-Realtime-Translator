namespace AwesomeRealtimeTranslator.Core;

/// <summary>whisper 解码出来的一段，带它在窗口里的结束时间。</summary>
/// <param name="Text">这一段的文本</param>
/// <param name="EndSeconds">相对窗口起点的结束时间，用来决定可以丢掉多少音频</param>
public readonly record struct DecodedSegment(string Text, double EndSeconds);

/// <param name="ShouldFinalize">这一步要不要发 Finalized</param>
/// <param name="Text">要定稿的文本</param>
/// <param name="TrimSeconds">从窗口头部丢掉多少秒音频（<see cref="DropAll"/> 为 true 时无意义）</param>
/// <param name="DropAll">true = 整个已解码窗口都可以丢（真静音或窗口到顶）</param>
public readonly record struct FinalizeDecision(
    bool ShouldFinalize,
    string Text,
    double TrimSeconds,
    bool DropAll)
{
    public static FinalizeDecision None => new(false, string.Empty, 0, false);
}

/// <summary>
/// 决定滑动窗口什么时候定稿。<b>从 WhisperTranscriber 里抽出来的唯一理由是能单测</b>——
/// 和 <see cref="RevealPacer"/> 同样的处理：这段判断是「译文多久出一次」的全部来源，
/// 而开发机上没有 Windows，不抽出来就只能靠猜。
///
/// <para><b>0.2.0 的 bug 长什么样</b>：原来只有两个定稿触发器——静音超过
/// <c>SilenceGap</c>(600ms)，或者窗口超过 <c>MaxWindowSeconds</c>(18s)。真实会议里
/// 对方连着讲，没有 600ms 的空档，于是唯一生效的是 18 秒上限。实测录像里译文
/// <b>连续 8 秒一个字没动</b>，英文在屏幕上堆到三行，然后两句译文一起砸出来。
/// 屏幕上明明已经显示出 <c>...on the backend. But we really looked at...</c>，
/// 那个句号就在那儿，但它在 volatile 里，没有任何人去看它。</para>
///
/// <para><b>这一版加的第三个触发器</b>：解码结果里出现句末标点、并且该前缀连续
/// <see cref="StableDecodesToCommit"/> 次解码没变，就把前缀定稿、窗口裁到那之后。
/// 两个独立的保险：</para>
/// <list type="number">
/// <item>候选边界后面必须<b>还有一段</b>。whisper 已经解码过那个边界并继续往后走了，
///       回头改写它的概率远低于改写正在生成的末段。</item>
/// <item>前缀要连续若干次解码完全一致。</item>
/// </list>
///
/// <para><b>这里刻意不做句子边界的最终判断。</b>遇到 <c>Dr.</c> 这种缩写，本策略会
/// 当成边界把音频裁掉——没关系：定稿出去的文本以 <c>Dr.</c> 结尾，
/// <see cref="Segmenter"/> 的缩写表会拒绝在那里切句，它留在 buffer 里等下一段拼上。
/// 本策略只回答「哪段音频可以安全退休」，<see cref="Segmenter"/> 才是句子边界的权威。
/// 两边职责分开之后，这里误判的代价只是多留几秒音频，不会切坏句子。</para>
/// </summary>
public sealed class FinalizePolicy
{
    /// <summary>
    /// 前缀要连续几次解码不变才敢定稿。
    ///
    /// 每次解码间隔一个 StepInterval(700ms)，所以 2 意味着句子说完之后约 1.4 秒定稿。
    /// 原本打算取 3（2.1s）保守一点，取 2 的理由是「候选边界后面必须还有一段」
    /// 这条已经独立提供了很强的稳定性保证，再叠一层只是白白加延迟——而整个产品
    /// 卖的就是端到端 1.5 秒。真机上发现回改，再往上调。
    /// </summary>
    public const int StableDecodesToCommit = 2;

    private static readonly char[] Terminators = ['.', '!', '?', '。', '！', '？', '…'];

    /// <summary>句末标点后面可能跟着的收尾符号，它们属于前一句。</summary>
    private const string ClosingMarks = "\"'”’)）》」";

    private readonly double _silenceGap;
    private readonly double _maxWindowSeconds;
    private readonly int _stableDecodes;

    private string _candidate = string.Empty;
    private int _candidateCount;

    public FinalizePolicy(double silenceGap, double maxWindowSeconds, int stableDecodes = StableDecodesToCommit)
    {
        _silenceGap = silenceGap;
        _maxWindowSeconds = maxWindowSeconds;
        _stableDecodes = Math.Max(1, stableDecodes);
    }

    /// <summary>会话重启或窗口清空时调用。</summary>
    public void Reset()
    {
        _candidate = string.Empty;
        _candidateCount = 0;
    }

    /// <param name="segments">这一步解码出来的全部段，按时间升序</param>
    /// <param name="silentFor">距离最后一次听到人声多少秒</param>
    /// <param name="windowSeconds">当前窗口的音频时长</param>
    public FinalizeDecision Evaluate(
        IReadOnlyList<DecodedSegment> segments,
        double silentFor,
        double windowSeconds)
    {
        // 真静音、或者窗口到顶：整窗定稿。这两条和改动前一致。
        if (silentFor >= _silenceGap || windowSeconds >= _maxWindowSeconds)
        {
            Reset();
            return new FinalizeDecision(true, Concat(segments, segments.Count), 0, true);
        }

        int boundary = LastCommittableIndex(segments);
        if (boundary < 0)
        {
            Reset();
            return FinalizeDecision.None;
        }

        string candidate = Concat(segments, boundary + 1);
        if (candidate.Length == 0)
        {
            Reset();
            return FinalizeDecision.None;
        }

        if (candidate == _candidate)
        {
            _candidateCount++;
        }
        else
        {
            _candidate = candidate;
            _candidateCount = 1;
        }

        if (_candidateCount < _stableDecodes)
        {
            return FinalizeDecision.None;
        }

        double trim = segments[boundary].EndSeconds;
        Reset();
        return new FinalizeDecision(true, candidate, trim, false);
    }

    /// <summary>
    /// 最后一个「以句末标点结尾、并且后面还有段」的下标；没有则 -1。
    ///
    /// 取<b>最后一个</b>而不是第一个：一次能退休的音频越多越好，窗口越短解码越快。
    /// </summary>
    private static int LastCommittableIndex(IReadOnlyList<DecodedSegment> segments)
    {
        for (int i = segments.Count - 2; i >= 0; i--)
        {
            if (EndsSentence(segments[i].Text) && segments[i].EndSeconds > 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool EndsSentence(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan().TrimEnd();

        // 把结尾的引号/括号剥掉再看标点："He said no." 的最后一个字符是引号
        while (span.Length > 0 && ClosingMarks.Contains(span[^1]))
        {
            span = span[..^1];
        }

        return span.Length > 0 && Terminators.Contains(span[^1]);
    }

    private static string Concat(IReadOnlyList<DecodedSegment> segments, int count)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count && i < segments.Count; i++)
        {
            sb.Append(segments[i].Text);
        }

        return sb.ToString().Trim();
    }
}
