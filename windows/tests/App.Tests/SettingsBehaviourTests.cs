// 显式写出来：这个工程摘掉了 System.Drawing / System.Windows.Forms 两个
// 全局 using（见 csproj），连带 System.IO 也不在隐式集合里了。
using System.IO;
using AwesomeRealtimeTranslator.Core;
using AwesomeRealtimeTranslator.Settings;
using Xunit;
using Forms = System.Windows.Forms;

namespace AwesomeRealtimeTranslator.Ui.Tests;

/// <summary>
/// 设置窗的处理器真的改到设置了吗，以及设置真的落盘了吗。
///
/// 这些回调从 0.1.0 到 0.1.5 <b>一次都没有被执行过</b>——设置窗根本打不开，
/// 没有人动过里面任何一个滑块。它们能编译，不代表它们能用。
/// </summary>
public class SettingsBehaviourTests
{
    private static AppSettings Defaults() => new(new InMemorySettingsStore());

    [Fact]
    public void MovingTheFontSizeSliderUpdatesSettingsAndLabel()
    {
        Sta.Run(() =>
        {
            AppSettings settings = Defaults();
            SettingsWindow window = new(settings, @"C:\models");

            window.FontSizeSlider.Value = 24;

            Assert.Equal(24, settings.FontSize);
            Assert.Equal("24", window.FontSizeValue.Text);
        });
    }

    [Fact]
    public void MovingThePaceSliderUpdatesSettingsAndHint()
    {
        Sta.Run(() =>
        {
            AppSettings settings = Defaults();
            SettingsWindow window = new(settings, @"C:\models");
            string hintBefore = window.PaceHint.Text;

            window.PaceSlider.Value = 0.95;

            Assert.Equal(0.95, settings.RevealPace, precision: 3);
            Assert.Equal("0.95s", window.PaceValue.Text);

            // 提示文字是按档位变的，0.5 和 0.95 必须落在不同档
            Assert.NotEqual(hintBefore, window.PaceHint.Text);
        });
    }

    [Fact]
    public void MovingTheLinesAndOpacitySlidersUpdatesSettings()
    {
        Sta.Run(() =>
        {
            AppSettings settings = Defaults();
            SettingsWindow window = new(settings, @"C:\models");

            window.LinesSlider.Value = 6;
            window.OpacitySlider.Value = 0.5;

            Assert.Equal(6, settings.VisibleLines);
            Assert.Equal(0.5, settings.Opacity, precision: 3);
            Assert.Equal("6", window.LinesValue.Text);
        });
    }

    [Fact]
    public void ChangingDisplayModeUpdatesSettings()
    {
        Sta.Run(() =>
        {
            AppSettings settings = Defaults();
            SettingsWindow window = new(settings, @"C:\models");

            // Enum.GetValues 的顺序就是下拉框的顺序：SourceOnly、TranslationOnly、Bilingual
            window.DisplayModeBox.SelectedIndex = 0;
            Assert.Equal(DisplayMode.SourceOnly, settings.DisplayMode);

            window.DisplayModeBox.SelectedIndex = 1;
            Assert.Equal(DisplayMode.TranslationOnly, settings.DisplayMode);
        });
    }

    [Fact]
    public void ChangingSourceLanguageUpdatesSettings()
    {
        Sta.Run(() =>
        {
            AppSettings settings = Defaults();
            SettingsWindow window = new(settings, @"C:\models");

            Assert.Equal("en", settings.SourceLanguage);

            window.SourceLanguageBox.SelectedIndex = 1;

            Assert.NotEqual("en", settings.SourceLanguage);
            Assert.NotEmpty(settings.SourceLanguage);
        });
    }

    [Fact]
    public void TogglingCheckBoxesUpdatesSettings()
    {
        Sta.Run(() =>
        {
            AppSettings settings = Defaults();
            SettingsWindow window = new(settings, @"C:\models");

            window.ClickThroughBox.IsChecked = true;
            Assert.True(settings.ClickThrough);

            // 默认是开的，这里验「关掉真的关掉」——最严重的失败模式在这个开关上
            Assert.True(settings.HideFromScreenSharing);
            window.HideFromSharingBox.IsChecked = false;
            Assert.False(settings.HideFromScreenSharing);

            window.SaveTranscriptsBox.IsChecked = true;
            Assert.True(settings.SaveTranscripts);
        });
    }

    /// <summary>版本号要在界面上看得见。0.2.0 之前它只出现在日志里。</summary>
    [Fact]
    public void SettingsWindowShowsTheVersion()
    {
        Sta.Run(() =>
        {
            SettingsWindow window = new(Defaults(), @"C:\models");

            Assert.Contains(AppVersion.Current, window.VersionText.Text);

            // 顺手钉住格式：0.2.0 这样的三段式，不是 0.2.0.0
            Assert.Matches(@"^\d+\.\d+\.\d+$", AppVersion.Current);
        });
    }

    /// <summary>
    /// 托盘图标。0.1.0 到 0.1.5 一直是 Windows 通用图标，因为代码去输出目录
    /// 找一个 &lt;ApplicationIcon&gt; 从不会拷过去的文件。
    /// </summary>
    [Fact]
    public void TrayIconComesFromTheEmbeddedAppIcon()
    {
        string? resource = Array.Find(
            typeof(AwesomeRealtimeTranslator.App).Assembly.GetManifestResourceNames(),
            n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(resource);

        using System.Drawing.Icon icon = AwesomeRealtimeTranslator.App.LoadTrayIcon();

        // 兜底返回的 SystemIcons.Application 是 32x32，拿到小图标尺寸
        // 就说明走的是嵌入资源那条路
        Assert.Equal(Forms.SystemInformation.SmallIconSize.Width, icon.Width);
    }

    [Fact]
    public void FlushPersistsSettingsImmediately()
    {
        string path = Path.Combine(Path.GetTempPath(), $"art-test-{Guid.NewGuid():N}", "settings.json");

        try
        {
            using (var store = new FileSettingsStore(path))
            {
                store.Set("fontSize", 23.0);
                store.Flush();
            }

            Assert.True(File.Exists(path));
            using var reopened = new FileSettingsStore(path);
            Assert.Equal(23.0, reopened.GetDouble("fontSize"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>
    /// 延迟落盘是 0.1.4 加的：拖一次滑块会触发几十次 Set，不能每次都写盘。
    /// 这里验的是「停手之后它真的会自己写下去」——定时器要是没接上，
    /// 用户改完设置直接关窗，改动就全丢了。
    /// </summary>
    [Fact]
    public async Task DebouncedWriteLandsOnDiskWithoutAnExplicitFlush()
    {
        string path = Path.Combine(Path.GetTempPath(), $"art-test-{Guid.NewGuid():N}", "settings.json");

        try
        {
            using var store = new FileSettingsStore(path);
            store.Set("visibleLines", 8);

            Assert.False(File.Exists(path), "Set 不该当场落盘，那正是 0.1.4 要改掉的行为。");

            // 合并窗口是 300ms，留足余量
            await Task.Delay(1500);

            Assert.True(File.Exists(path), "停手之后定时器没有把改动写下去。");
            using var reopened = new FileSettingsStore(path);
            Assert.Equal(8, reopened.GetInt("visibleLines"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    /// <summary>Dispose 必须兜住最后那一下改动——退出路径靠的就是它。</summary>
    [Fact]
    public void DisposeFlushesPendingChanges()
    {
        string path = Path.Combine(Path.GetTempPath(), $"art-test-{Guid.NewGuid():N}", "settings.json");

        try
        {
            var store = new FileSettingsStore(path);
            store.Set("opacity", 0.42);
            store.Dispose();

            using var reopened = new FileSettingsStore(path);
            Assert.Equal(0.42, reopened.GetDouble("opacity"));
        }
        finally
        {
            Cleanup(path);
        }
    }

    private static void Cleanup(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // 临时目录清不掉不该让用例失败
        }
    }
}
