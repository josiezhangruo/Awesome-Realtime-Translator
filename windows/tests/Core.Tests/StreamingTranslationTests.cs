using AwesomeRealtimeTranslator.Core;
using AwesomeRealtimeTranslator.Core.Translation;
using Xunit;

namespace AwesomeRealtimeTranslator.Core.Tests;

/// <summary>
/// 流式译文（0.2.1）。OPUS-MT 的解码器本来就是逐 token 产出中文的，
/// 旧代码把这些 token 攒到 <c>&lt;/s&gt;</c> 才一次性返回，于是屏幕上是
/// 「… 等 N 秒 → 整句砰」。这里验的是「吐前缀」这件事本身的两个硬要求：
/// <b>严格有序</b>，和<b>哨兵不能露出来</b>。
/// </summary>
public class StreamingTranslationTests
{
    // MARK: - 有序性

    [Fact]
    public void SyncProgressReportsOnTheCallingThread()
    {
        // 这是整个流式译文正确性的地基。Progress<T> 是异步派发的，
        // 最后一个前缀有可能排在完整译文后面送达，把整句覆盖成半句。
        int callerThread = Environment.CurrentManagedThreadId;
        int observed = -1;

        IProgress<string> progress = new SyncProgress<string>(_ => observed = Environment.CurrentManagedThreadId);
        progress.Report("半句");

        Assert.Equal(callerThread, observed);
    }

    [Fact]
    public void SyncProgressPreservesOrder()
    {
        var seen = new List<string>();
        IProgress<string> progress = new SyncProgress<string>(seen.Add);

        foreach (string s in new[] { "我", "我们", "我们真", "我们真的" })
        {
            progress.Report(s);
        }

        Assert.Equal(["我", "我们", "我们真", "我们真的"], seen);
    }

    // MARK: - 哨兵不能在中间态露出来

    private static readonly Dictionary<string, string> Restore =
        new() { ["ZQX1"] = "容器编排" };

    [Theory]
    // 完整哨兵：正常还原
    [InlineData("来计算 ZQX1 的缩放", "来计算 容器编排 的缩放")]
    [InlineData("ZQX1", "容器编排")]
    // 残缺哨兵在结尾：砍掉，下一帧会补齐
    [InlineData("来计算 ZQX", "来计算 ")]
    [InlineData("来计算 ZQ", "来计算 ")]
    [InlineData("来计算 Z", "来计算 ")]
    // 不是哨兵的结尾不能被误砍
    [InlineData("来计算 A", "来计算 A")]
    [InlineData("我们真的研究了", "我们真的研究了")]
    [InlineData("", "")]
    public void PartialRestoreHidesHalfWrittenSentinels(string partial, string expected)
    {
        Assert.Equal(expected, TermProtector.RestorePartial(partial, Restore));
    }

    [Fact]
    public void PartialRestoreNeverRetractsWhatItAlreadyShowed()
    {
        // append-only 是产品硬约束（译文一旦出现就不再改写）。
        // 逐字喂一个带哨兵的译文，每一步的输出都必须以上一步为前缀。
        const string full = "用 ZQX1 来做";
        string previous = string.Empty;

        for (int i = 1; i <= full.Length; i++)
        {
            string shown = TermProtector.RestorePartial(full[..i], Restore);
            Assert.True(
                shown.StartsWith(previous, StringComparison.Ordinal),
                $"第 {i} 步把已经显示过的内容改了：'{previous}' → '{shown}'");
            previous = shown;
        }

        Assert.Equal("用 容器编排 来做", previous);
    }

    [Fact]
    public void PartialRestoreWithNoGlossaryIsIdentity()
    {
        Assert.Equal("我们真的研究了 ZQX", TermProtector.RestorePartial("我们真的研究了 ZQX", new Dictionary<string, string>()));
    }
}
