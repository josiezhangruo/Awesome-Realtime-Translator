using AwesomeRealtimeTranslator.Core;
using Xunit;

namespace AwesomeRealtimeTranslator.Core.Tests;

/// <summary>
/// <see cref="Segmenter.Configuration.SilenceFlushInterval"/> 和识别器发 volatile 的
/// 间隔之间有一条<b>没写下来的耦合</b>，0.2.0 栽在这里。
///
/// <para>WhisperTranscriber 每 700ms 重解码一次窗口、发一个 Volatile。
/// Segmenter 的静音判定看的是「距离上一个事件多久」。
/// 于是 <c>SilenceFlushInterval &lt; 0.7</c> 的任何取值，都会在<b>每两个 volatile 之间</b>
/// 触发一次静音定稿——它测的根本不是对方停顿，而是识别器的解码节拍。</para>
///
/// <para>0.2.0 把它设成了 0.45（本意是让译文早点出来），结果是每一句都在
/// 随机位置被切断。用户录像里那句
/// <c>So I didn't go into this too, too much last time and I</c>
/// 就是这么来的——切在 <c>and I</c> 上，OPUS-MT 拿到一个吊着的残片，
/// 吐出「所以我没有再讨论这个, 太多的上次,我」。</para>
/// </summary>
public class SilenceCouplingTests
{
    /// <summary>WhisperTranscriber.StepInterval。两边必须一起改。</summary>
    private const double VolatileCadence = 0.7;

    /// <summary>SessionPipeline 实际用的值。</summary>
    private const double ProductionSilenceFlush = 1.4;

    private static Segmenter MakeSegmenter(double silence) =>
        new(new Segmenter.Configuration { SilenceFlushInterval = silence });

    private const string HalfSentence = "So I didn't go into this too, too much last time and I";

    [Fact]
    public void ProductionSettingSurvivesTheVolatileCadence()
    {
        Segmenter s = MakeSegmenter(ProductionSilenceFlush);

        // 上一次定稿留下半句（没有句末标点，Cut 不会切它）
        Assert.Empty(s.Ingest(TranscriptEvent.Finalized(HalfSentence), 0.0));
        Assert.Equal(HalfSentence, s.PendingText);

        // 对方还在说：volatile 每 0.7s 来一次，ticker 每 100ms 查一次
        double t = 0.0;
        for (int i = 0; i < 10; i++)
        {
            t += VolatileCadence;
            s.Ingest(TranscriptEvent.Volatile("can share this information"), t);

            for (double u = t + 0.1; u < t + VolatileCadence; u += 0.1)
            {
                Assert.Empty(s.Tick(u));
            }
        }
    }

    [Fact]
    public void TooShortAnIntervalChopsMidClause()
    {
        // 0.2.0 的实际行为，钉在这里当反例。
        Segmenter s = MakeSegmenter(0.45);
        s.Ingest(TranscriptEvent.Finalized(HalfSentence), 0.0);

        IReadOnlyList<Sentence> chopped = s.Tick(0.5);

        Assert.Equal([HalfSentence], chopped.Select(x => x.Text).ToArray());
    }

    [Fact]
    public void RealSilenceStillFlushes()
    {
        // 修完之后静音定稿必须still有效，否则对方说完最后半句就永远不出译文。
        Segmenter s = MakeSegmenter(ProductionSilenceFlush);
        s.Ingest(TranscriptEvent.Finalized(HalfSentence), 0.0);

        Assert.Empty(s.Tick(1.0));

        IReadOnlyList<Sentence> flushed = s.Tick(1.5);
        Assert.Equal([HalfSentence], flushed.Select(x => x.Text).ToArray());
    }

    [Fact]
    public void IntervalMustExceedTheVolatileCadence()
    {
        // 把这条耦合写成断言，免得以后有人再去调小它。
        Assert.True(
            ProductionSilenceFlush > VolatileCadence,
            "SilenceFlushInterval 必须大于识别器发 volatile 的间隔，否则静音判定测的是解码节拍");
    }
}
