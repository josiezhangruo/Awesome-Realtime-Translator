using AwesomeRealtimeTranslator.Core;
using Xunit;

namespace AwesomeRealtimeTranslator.Core.Tests;

/// <summary>
/// 这一组用例对应 0.2.1 修的那个事故：连续说话时译文最长 18 秒不动。
///
/// 用例里的台词取自用户发来的那段真实会议录像，不是编的——
/// 那段录像里屏幕上明明显示着 <c>...on the backend. But we really looked at...</c>，
/// 句号就在那儿，但旧代码只在静音或 18 秒窗口上限时才定稿，于是什么都没发生。
/// </summary>
public class FinalizePolicyTests
{
    private const double SilenceGap = 0.6;
    private const double MaxWindow = 7.0;

    private static FinalizePolicy MakePolicy(int stable = 2) =>
        new(SilenceGap, MaxWindow, stable);

    private static DecodedSegment[] Segments(params (string Text, double End)[] parts) =>
        parts.Select(p => new DecodedSegment(p.Text, p.End)).ToArray();

    // MARK: - 原有的两个触发器不能被改坏

    [Fact]
    public void SilenceFinalizesWholeWindow()
    {
        FinalizePolicy policy = MakePolicy();
        FinalizeDecision d = policy.Evaluate(
            Segments((" Tell me about", 1.0), (" yourself", 2.0)),
            silentFor: 0.8,
            windowSeconds: 2.0);

        Assert.True(d.ShouldFinalize);
        Assert.True(d.DropAll);
        Assert.Equal("Tell me about yourself", d.Text);
    }

    [Fact]
    public void WindowCapFinalizesWholeWindow()
    {
        FinalizePolicy policy = MakePolicy();
        FinalizeDecision d = policy.Evaluate(
            Segments((" and then we", 4.0), (" looked at", 7.5)),
            silentFor: 0.1,
            windowSeconds: 7.5);

        Assert.True(d.ShouldFinalize);
        Assert.True(d.DropAll);
        Assert.Equal("and then we looked at", d.Text);
    }

    // MARK: - 新加的标点触发器

    [Fact]
    public void NoPunctuationNeverCommits()
    {
        FinalizePolicy policy = MakePolicy();

        for (int i = 0; i < 5; i++)
        {
            FinalizeDecision d = policy.Evaluate(
                Segments((" can share this information", 2.0), (" with all of you", 4.0)),
                silentFor: 0.1,
                windowSeconds: 4.0);
            Assert.False(d.ShouldFinalize);
        }
    }

    [Fact]
    public void PunctuationCommitsOnceStable()
    {
        FinalizePolicy policy = MakePolicy(stable: 2);
        DecodedSegment[] segments = Segments(
            (" them on the backend.", 3.2),
            (" But we really looked at", 5.0));

        Assert.False(policy.Evaluate(segments, 0.1, 5.0).ShouldFinalize);

        FinalizeDecision second = policy.Evaluate(segments, 0.1, 5.0);
        Assert.True(second.ShouldFinalize);
        Assert.False(second.DropAll);
        Assert.Equal("them on the backend.", second.Text);
        Assert.Equal(3.2, second.TrimSeconds);
    }

    [Fact]
    public void LastSegmentIsNeverCommitted()
    {
        // 末段还在生成，它的尾巴最不可靠。即便它以句号结尾也不定稿，
        // 等下一步解码出现新的一段再说。
        FinalizePolicy policy = MakePolicy(stable: 1);
        FinalizeDecision d = policy.Evaluate(
            Segments((" But we really looked at how leading brands communicate.", 5.0)),
            silentFor: 0.1,
            windowSeconds: 5.0);

        Assert.False(d.ShouldFinalize);
    }

    [Fact]
    public void RewrittenPrefixRestartsStabilityCount()
    {
        // whisper 回头改写了前缀：计数必须归零重来，否则会把改了一半的文本定稿出去，
        // 而定稿是不可撤回的（译文一旦出现就不再改写是产品硬约束）。
        FinalizePolicy policy = MakePolicy(stable: 2);

        Assert.False(policy.Evaluate(
            Segments((" them on the back end.", 3.2), (" But we", 5.0)), 0.1, 5.0).ShouldFinalize);

        Assert.False(policy.Evaluate(
            Segments((" them on the backend.", 3.2), (" But we", 5.0)), 0.1, 5.0).ShouldFinalize);

        FinalizeDecision third = policy.Evaluate(
            Segments((" them on the backend.", 3.2), (" But we", 5.0)), 0.1, 5.0);
        Assert.True(third.ShouldFinalize);
        Assert.Equal("them on the backend.", third.Text);
    }

    [Fact]
    public void CommitsUpToTheLastBoundaryNotTheFirst()
    {
        // 一次退休的音频越多越好：窗口越短，下一次解码越快。
        FinalizePolicy policy = MakePolicy(stable: 1);
        FinalizeDecision d = policy.Evaluate(
            Segments(
                (" The second part was the brand inspiration piece.", 2.0),
                (" But we really looked at how leading brands communicate.", 4.5),
                (" So not just other appliance brands", 6.0)),
            silentFor: 0.1,
            windowSeconds: 6.0);

        Assert.True(d.ShouldFinalize);
        Assert.Equal(
            "The second part was the brand inspiration piece. " +
            "But we really looked at how leading brands communicate.",
            d.Text);
        Assert.Equal(4.5, d.TrimSeconds);
    }

    [Fact]
    public void ClosingQuoteAfterTerminatorStillCounts()
    {
        FinalizePolicy policy = MakePolicy(stable: 1);
        FinalizeDecision d = policy.Evaluate(
            Segments((" He said \"no.\"", 2.0), (" Then he left", 3.0)),
            silentFor: 0.1,
            windowSeconds: 3.0);

        Assert.True(d.ShouldFinalize);
        Assert.Equal("He said \"no.\"", d.Text);
    }

    [Fact]
    public void AbbreviationIsCommittedAndLeftToTheSegmenter()
    {
        // 本策略只管「哪段音频能退休」，不管句子边界。"Dr." 被当成边界把音频裁掉，
        // 代价为零：Segmenter 的缩写表会拒绝在这里切句，文本留在 buffer 里等下一段。
        FinalizePolicy policy = MakePolicy(stable: 1);
        FinalizeDecision d = policy.Evaluate(
            Segments((" I spoke to Dr.", 2.0), (" Smith about it", 3.0)),
            silentFor: 0.1,
            windowSeconds: 3.0);

        Assert.True(d.ShouldFinalize);
        Assert.Equal("I spoke to Dr.", d.Text);

        var segmenter = new Segmenter();
        IReadOnlyList<Sentence> sentences = segmenter.Ingest(TranscriptEvent.Finalized(d.Text), 1.0);
        Assert.Empty(sentences);
        Assert.Equal("I spoke to Dr.", segmenter.PendingText);
    }

    [Fact]
    public void StabilityCountResetsAfterCommit()
    {
        FinalizePolicy policy = MakePolicy(stable: 2);
        DecodedSegment[] first = Segments((" First one.", 2.0), (" Second", 3.0));

        policy.Evaluate(first, 0.1, 3.0);
        Assert.True(policy.Evaluate(first, 0.1, 3.0).ShouldFinalize);

        // 定稿之后计数归零：同样的输入要再攒满一次才会重复定稿
        Assert.False(policy.Evaluate(first, 0.1, 3.0).ShouldFinalize);
    }

    [Fact]
    public void ZeroEndTimeIsNotCommittable()
    {
        // 裁 0 秒等于没裁，窗口不会缩短，下一步会原地重复定稿同一段文本。
        FinalizePolicy policy = MakePolicy(stable: 1);
        FinalizeDecision d = policy.Evaluate(
            Segments((" Yes.", 0.0), (" And then", 1.0)),
            silentFor: 0.1,
            windowSeconds: 1.0);

        Assert.False(d.ShouldFinalize);
    }

    // MARK: - 事故重演

    [Fact]
    public void EightSecondsOfContinuousSpeechNowProducesText()
    {
        // 录像里的真实序列：连续说话，没有 600ms 静音，窗口没到上限。
        // 旧代码在这整段里一个 Finalized 都不发，译文 8 秒不动。
        FinalizePolicy policy = MakePolicy();
        var finalized = new List<string>();

        (string Text, double End)[][] steps =
        [
            [(" can share this information", 1.4)],
            [(" can share this information with all of you", 2.1)],
            [(" can share this information with all of you if you're interested", 2.8)],
            [(" can share this information with all of you if you're interested and know", 3.5)],
            [(" can share this information with all of you if you're interested and know them on the backend.", 4.2)],
            [(" can share this information with all of you if you're interested and know them on the backend.", 4.2),
             (" But we really looked at", 4.9)],
            [(" can share this information with all of you if you're interested and know them on the backend.", 4.2),
             (" But we really looked at how leading", 5.6)],
        ];

        double windowSeconds = 0;
        foreach ((string, double)[] step in steps)
        {
            DecodedSegment[] segments = Segments(step);
            windowSeconds = segments[^1].EndSeconds;
            FinalizeDecision d = policy.Evaluate(segments, silentFor: 0.1, windowSeconds);
            if (d.ShouldFinalize)
            {
                finalized.Add(d.Text);
            }
        }

        Assert.True(windowSeconds < MaxWindow, "这个序列不能触碰窗口上限，否则测的就不是新触发器了");
        Assert.Equal(
            ["can share this information with all of you if you're interested and know them on the backend."],
            finalized);
    }
}
