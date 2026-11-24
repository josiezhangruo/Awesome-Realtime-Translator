using AwesomeRealtimeTranslator.Caption;
using AwesomeRealtimeTranslator.Core;
using AwesomeRealtimeTranslator.Settings;
using Xunit;

namespace AwesomeRealtimeTranslator.Ui.Tests;

/// <summary>
/// 把两个窗口真的构造一遍。
///
/// <b>这个文件是为一个具体的事故存在的。</b>0.1.0 到 0.1.4 之间，点「设置」
/// 在每一台 Windows 机器上都是必崩：XAML 解析器给 <c>&lt;Slider Minimum="11"&gt;</c>
/// 设最小值时，WPF 把 Value 从 0 强制抬到 11，当场触发 ValueChanged，
/// 而回调要写的 TextBlock 在 XAML 里排在滑块后面、还是 null。
///
/// 代价是四个版本、一个真实用户、两轮错误的诊断。而要抓住它，只需要
/// <b>把这个窗口构造一次</b>——不用显示、不用点、不用截图。构造不显示窗口，
/// 所以在没有交互桌面的 CI runner 上也照样跑。
///
/// 所以这些用例不追求覆盖率，只守一条线：<b>每个 XAML 窗口都必须能被 new 出来。</b>
/// 新加窗口就往这里加一个用例。
/// </summary>
public class WindowConstructionTests
{
    private static AppSettings Defaults() => new(new InMemorySettingsStore());

    [Fact]
    public void SettingsWindowConstructs()
    {
        Sta.Run(() =>
        {
            SettingsWindow window = new(Defaults(), @"C:\models");
            Assert.NotNull(window);
        });
    }

    /// <summary>
    /// 光「不抛异常」还不够。守卫要是加错了位置——比如 _loading 一直挡到
    /// 构造结束——窗口能建起来，但每个数值旁边是空白。那是另一种坏，
    /// 而且更难发现。所以把两头都钉住。
    /// </summary>
    [Fact]
    public void SettingsWindowLabelsReflectCurrentSettings()
    {
        Sta.Run(() =>
        {
            SettingsWindow window = new(Defaults(), @"C:\models");

            Assert.Equal("17", window.FontSizeValue.Text);
            Assert.Equal("3", window.LinesValue.Text);
            Assert.Equal("0.50s", window.PaceValue.Text);
            Assert.NotEmpty(window.OpacityValue.Text);
            Assert.NotEmpty(window.PaceHint.Text);
            Assert.Contains(@"C:\models", window.ModelPathText.Text);
        });
    }

    /// <summary>滑块本身也得真的被赋上值，否则用户一开窗就看到四个滑块归零。</summary>
    [Fact]
    public void SettingsWindowSlidersReflectCurrentSettings()
    {
        Sta.Run(() =>
        {
            SettingsWindow window = new(Defaults(), @"C:\models");

            Assert.Equal(17, window.FontSizeSlider.Value);
            Assert.Equal(3, window.LinesSlider.Value);
            Assert.Equal(0.5, window.PaceSlider.Value, precision: 3);
            Assert.Equal(0.82, window.OpacitySlider.Value, precision: 3);
        });
    }

    /// <summary>
    /// 非默认值走一遍：构造期的事件顺序和默认值那条路不完全一样
    /// （赋值会真的改变 Value，而不是正好等于强制后的结果）。
    /// </summary>
    [Fact]
    public void SettingsWindowConstructsWithNonDefaultSettings()
    {
        Sta.Run(() =>
        {
            var store = new InMemorySettingsStore();
            store.Set("fontSize", 28.0);
            store.Set("visibleLines", 7);
            store.Set("revealPace", 0.9);
            store.Set("opacity", 0.35);
            store.Set("displayMode", nameof(DisplayMode.TranslationOnly));
            store.Set("sourceLanguage", "fr");

            SettingsWindow window = new(new AppSettings(store), @"D:\somewhere\models");

            Assert.Equal("28", window.FontSizeValue.Text);
            Assert.Equal("7", window.LinesValue.Text);
            Assert.Equal("0.90s", window.PaceValue.Text);
        });
    }

    [Fact]
    public void CaptionWindowConstructs()
    {
        Sta.Run(() =>
        {
            CaptionWindow window = new(
                new CaptionStore(),
                Defaults(),
                onToggle: () => Task.CompletedTask,
                onOpenSettings: () => { },
                onQuit: () => { });

            Assert.NotNull(window.VisibleLines);
        });
    }
}
