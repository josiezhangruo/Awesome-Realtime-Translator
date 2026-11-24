using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AwesomeRealtimeTranslator.Interop;
using Xunit;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AwesomeRealtimeTranslator.Ui.Tests;

/// <summary>
/// <b>真的截一次屏，看字幕窗在不在里面。</b>
///
/// 「字幕窗不会出现在屏幕共享里」是这整个产品最关键的承诺，也是最严重的
/// 失败模式——以为对方看不到、其实看得到。而从 0.1.0 到 0.1.5，<b>没有任何人
/// 验证过它</b>：代码里只有一次 <c>GetWindowDisplayAffinity</c> 回读，
/// 而「回读说设上了」和「截图里真的没有」之间差着一整个未知。
///
/// 这里把那个未知补上，用的是 <c>Graphics.CopyFromScreen</c>——它底下就是
/// BitBlt，而 <c>WDA_EXCLUDEFROMCAPTURE</c> 正是用来挡住这条路的。
///
/// <b>对照组是这个用例的命门。</b>一个截不到任何东西的 runner 上，
/// 「图里找不到测试窗」会被误读成「排除生效了」，得到一个假绿——那比
/// 没有这个测试更糟，因为它会让人以为验过了。所以先不排除截一次：
/// 找不到就说明这台机器的截屏本身不工作，当场 Skip 并说清原因。
///
/// <b>它证明不了什么：</b>这验的是 GDI 截屏这一条路。Zoom、Teams、
/// 飞书的桌面客户端用的采集方式不止一种（Desktop Duplication、
/// Windows Graphics Capture、各家自己的驱动）。所以 README 里那句
/// 「共享前自己用会议软件的共享预览确认一次」不能撤。
/// </summary>
public class ScreenCaptureTests
{
    /// <summary>纯品红。屏幕上几乎不会自然出现，好认。</summary>
    private static readonly Color ProbeColor = Color.FromRgb(255, 0, 255);

    private const int ProbeWidth = 420;
    private const int ProbeHeight = 320;

    [SkippableFact]
    public void ExcludedWindowIsAbsentFromScreenCapture()
    {
        Sta.Run(() =>
        {
            Skip.If(Forms.Screen.PrimaryScreen is null, "这台机器没有主显示器，截屏用例不作数。");

            // 刻意不用真的 CaptionWindow：它的颜色和不透明度都是用户可调的，
            // 半透明黑在截图里认不出来。要验的是 Win32 机制本身，
            // 所以把被测对象做成一个一眼能找到的纯色窗。
            // 字幕窗自己有没有把这个标志设上，由 Win32BehaviourTests 验。
            var probe = new Window
            {
                Title = "capture probe",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Left = 0,
                Top = 0,
                Width = ProbeWidth,
                Height = ProbeHeight,
                Background = new SolidColorBrush(ProbeColor),
            };

            try
            {
                Sta.ShowAndRender(probe);
                IntPtr hwnd = new WindowInteropHelper(probe).Handle;
                Assert.NotEqual(IntPtr.Zero, hwnd);

                // ---- 对照组：没有排除的时候，截图里必须找得到它 ----
                Win32.SetExcludedFromCapture(hwnd, false);
                Sta.Settle(probe);

                long visible = CountProbePixels();
                Skip.If(
                    visible == 0,
                    "对照组就没在截图里找到测试窗——这台 runner 的屏幕截取本身不工作" +
                    "（无交互会话 / 没有合成器）。本用例不作数，不能当成通过。");

                // ---- 真正要验的 ----
                Assert.True(
                    Win32.SetExcludedFromCapture(hwnd, true),
                    "SetWindowDisplayAffinity 调用失败，这个系统可能低于 Windows 10 2004。");
                Assert.True(Win32.IsExcludedFromCapture(hwnd), "回读没确认 EXCLUDEFROMCAPTURE。");

                Sta.Settle(probe);
                long afterExclusion = CountProbePixels();

                Assert.Equal(0, afterExclusion);
            }
            finally
            {
                probe.Close();
            }
        });
    }

    /// <summary>
    /// 反过来再验一次：把排除关掉，窗口必须重新出现在截图里。
    ///
    /// 这条防的是另一种假绿——万一截屏在这台机器上永远返回纯黑，
    /// 上面那个用例会通过，而对照组是在同一次运行里测的，理论上能挡住。
    /// 但「关掉之后又看得见」这件事本身也是功能的一部分：
    /// 用户在设置里关掉这个开关，是期望它真的关掉的。
    /// </summary>
    [SkippableFact]
    public void TurningExclusionOffMakesWindowVisibleAgain()
    {
        Sta.Run(() =>
        {
            Skip.If(Forms.Screen.PrimaryScreen is null, "这台机器没有主显示器，截屏用例不作数。");

            var probe = new Window
            {
                Title = "capture probe",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Left = 0,
                Top = 0,
                Width = ProbeWidth,
                Height = ProbeHeight,
                Background = new SolidColorBrush(ProbeColor),
            };

            try
            {
                Sta.ShowAndRender(probe);
                IntPtr hwnd = new WindowInteropHelper(probe).Handle;

                Win32.SetExcludedFromCapture(hwnd, true);
                Sta.Settle(probe);
                long hidden = CountProbePixels();

                Win32.SetExcludedFromCapture(hwnd, false);
                Sta.Settle(probe);
                long shown = CountProbePixels();

                Skip.If(shown == 0, "这台 runner 的屏幕截取不工作，本用例不作数。");

                Assert.Equal(0, hidden);
                Assert.True(shown > 0);
            }
            finally
            {
                probe.Close();
            }
        });
    }

    /// <summary>
    /// 截一张主屏，数里面有多少个品红像素。
    ///
    /// 用 LockBits 而不是 GetPixel：一张 1080p 要扫两百万个点，
    /// GetPixel 会让这个用例跑成分钟级。
    /// </summary>
    private static long CountProbePixels()
    {
        Drawing.Rectangle bounds = Forms.Screen.PrimaryScreen!.Bounds;

        // PixelFormat 在 WPF 和 GDI 两边都有同名类型，所以这里一路写全限定名
        using var shot = new Drawing.Bitmap(
            bounds.Width, bounds.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (Drawing.Graphics g = Drawing.Graphics.FromImage(shot))
        {
            g.CopyFromScreen(
                bounds.Location,
                Drawing.Point.Empty,
                bounds.Size,
                Drawing.CopyPixelOperation.SourceCopy);
        }

        Drawing.Imaging.BitmapData data = shot.LockBits(
            new Drawing.Rectangle(0, 0, shot.Width, shot.Height),
            Drawing.Imaging.ImageLockMode.ReadOnly,
            Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            long count = 0;
            unsafe
            {
                for (int y = 0; y < data.Height; y++)
                {
                    byte* row = (byte*)data.Scan0 + ((long)y * data.Stride);
                    for (int x = 0; x < data.Width; x++)
                    {
                        // 32bppArgb 在内存里是 BGRA
                        byte b = row[(x * 4) + 0];
                        byte gr = row[(x * 4) + 1];
                        byte r = row[(x * 4) + 2];

                        // 容差留宽一点：缩放、色彩管理和 JPEG 式的处理都可能让
                        // 纯品红偏一两个数值。但绿色通道必须明显低，
                        // 这样白色和浅色背景不会被算进来。
                        if (r > 200 && b > 200 && gr < 60)
                        {
                            count++;
                        }
                    }
                }
            }

            return count;
        }
        finally
        {
            shot.UnlockBits(data);
        }
    }
}
