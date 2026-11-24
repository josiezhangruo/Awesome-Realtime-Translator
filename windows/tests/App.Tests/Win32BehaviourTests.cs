using System.Windows.Interop;
using AwesomeRealtimeTranslator.Caption;
using AwesomeRealtimeTranslator.Core;
using AwesomeRealtimeTranslator.Interop;
using Xunit;

namespace AwesomeRealtimeTranslator.Ui.Tests;

/// <summary>
/// 真字幕窗显示之后，窗口样式到底被设成了什么。
///
/// 这几条是 Windows 版能成立的全部前提，而它们<b>全都发生在
/// <c>OnSourceInitialized</c> 里</b>——也就是只有窗口真的被 Show() 出来、
/// 拿到 HWND 之后才会执行。构造测试碰不到这一段，Mac 开发机更碰不到。
/// 从 0.1.0 到 0.1.5，这四条没有一条被验证过。
///
/// 和 <see cref="ScreenCaptureTests"/> 分工：那边验「Win32 机制真的能挡住截屏」，
/// 这边验「我们的窗口真的用上了那个机制」。两件事都错过，产品就是在骗人。
/// </summary>
public class Win32BehaviourTests
{
    private static CaptionWindow MakeCaption(AppSettings settings) => new(
        new CaptionStore(),
        settings,
        onToggle: () => Task.CompletedTask,
        onOpenSettings: () => { },
        onQuit: () => { });

    private static AppSettings SettingsWith(Action<InMemorySettingsStore> configure)
    {
        var store = new InMemorySettingsStore();
        configure(store);
        return new AppSettings(store);
    }

    /// <summary>产品最关键的承诺：开关打开时，字幕窗必须真的被设成「排除出截屏」。</summary>
    [Fact]
    public void CaptionWindowSetsExcludeFromCaptureWhenEnabled()
    {
        Sta.Run(() =>
        {
            AppSettings settings = SettingsWith(s => s.Set("hideFromScreenSharing", true));
            CaptionWindow window = MakeCaption(settings);

            try
            {
                Sta.ShowAndRender(window);
                IntPtr hwnd = new WindowInteropHelper(window).Handle;

                Assert.True(
                    Win32.GetWindowDisplayAffinity(hwnd, out uint affinity),
                    "GetWindowDisplayAffinity 调用失败。");
                Assert.Equal(Win32.WDA_EXCLUDEFROMCAPTURE, affinity);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>用户把开关关掉，就必须真的关掉——不能悄悄留着。</summary>
    [Fact]
    public void CaptionWindowLeavesCaptureAloneWhenDisabled()
    {
        Sta.Run(() =>
        {
            AppSettings settings = SettingsWith(s => s.Set("hideFromScreenSharing", false));
            CaptionWindow window = MakeCaption(settings);

            try
            {
                Sta.ShowAndRender(window);
                IntPtr hwnd = new WindowInteropHelper(window).Handle;

                Assert.True(Win32.GetWindowDisplayAffinity(hwnd, out uint affinity));
                Assert.Equal(Win32.WDA_NONE, affinity);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 不抢键盘焦点（面试时打字不能被打断），且不进任务栏和 Alt-Tab。
    /// 对应 macOS 的 canBecomeKey = false 和 LSUIElement。
    /// </summary>
    [Fact]
    public void CaptionWindowIsNonActivatingAndHiddenFromTaskbar()
    {
        Sta.Run(() =>
        {
            CaptionWindow window = MakeCaption(new AppSettings(new InMemorySettingsStore()));

            try
            {
                Sta.ShowAndRender(window);
                IntPtr hwnd = new WindowInteropHelper(window).Handle;

                int style = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);

                Assert.True(
                    (style & Win32.WS_EX_NOACTIVATE) != 0,
                    "WS_EX_NOACTIVATE 没设上——点字幕窗会把焦点从会议软件抢走。");
                Assert.True(
                    (style & Win32.WS_EX_TOOLWINDOW) != 0,
                    "WS_EX_TOOLWINDOW 没设上——字幕窗会出现在任务栏和 Alt-Tab 里。");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>鼠标穿透：开了之后点击要落到后面的窗口上，关了要能恢复。</summary>
    [Fact]
    public void ClickThroughTogglesTransparentStyle()
    {
        Sta.Run(() =>
        {
            AppSettings settings = SettingsWith(s => s.Set("clickThrough", false));
            CaptionWindow window = MakeCaption(settings);

            try
            {
                Sta.ShowAndRender(window);
                IntPtr hwnd = new WindowInteropHelper(window).Handle;

                Assert.True(
                    (Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE) & Win32.WS_EX_TRANSPARENT) == 0,
                    "默认不该是穿透的。");

                settings.ClickThrough = true;
                Sta.Settle(window);
                Assert.True(
                    (Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE) & Win32.WS_EX_TRANSPARENT) != 0,
                    "打开穿透之后 WS_EX_TRANSPARENT 没设上。");

                settings.ClickThrough = false;
                Sta.Settle(window);
                Assert.True(
                    (Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE) & Win32.WS_EX_TRANSPARENT) == 0,
                    "关掉穿透之后 WS_EX_TRANSPARENT 没撤掉——用户再也点不到字幕窗了。");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 全局快捷键能注册上。
    ///
    /// 注册失败是预期内的情况（别的程序占了同一个组合），代码里也有提示。
    /// 这里验的是另一件事：我们传给 RegisterHotKey 的参数本身是合法的。
    /// 传错 modifier 或 vk 会一样地失败，而那种失败会被当成「被占用了」，
    /// 永远查不出来。
    /// </summary>
    [Fact]
    public void GlobalHotKeysUseValidParameters()
    {
        Sta.Run(() =>
        {
            CaptionWindow window = MakeCaption(new AppSettings(new InMemorySettingsStore()));

            try
            {
                Sta.ShowAndRender(window);
                IntPtr hwnd = new WindowInteropHelper(window).Handle;

                // 用不同的 id，免得和应用自己注册的撞上
                const int probeId = 9001;
                uint modifiers = Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_NOREPEAT;

                bool registered = Win32.RegisterHotKey(hwnd, probeId, modifiers, 'T');
                try
                {
                    Assert.True(
                        registered,
                        "Ctrl+Alt+T 注册失败。在干净的 CI runner 上没人会占用它，" +
                        "所以这更可能是 modifier 或虚拟键码传错了。");
                }
                finally
                {
                    if (registered)
                    {
                        Win32.UnregisterHotKey(hwnd, probeId);
                    }
                }
            }
            finally
            {
                window.Close();
            }
        });
    }
}
