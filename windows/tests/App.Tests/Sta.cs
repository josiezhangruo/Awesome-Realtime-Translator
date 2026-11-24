namespace AwesomeRealtimeTranslator.Ui.Tests;

using System.Runtime.ExceptionServices;

/// <summary>
/// 在真正的 STA 线程上跑一段代码。
///
/// WPF 的任何 <see cref="System.Windows.DependencyObject"/> 都只能在 STA 线程上创建，
/// 而 xunit 的测试线程是 MTA，直接 new 一个 Window 会抛
/// InvalidOperationException。
///
/// 没有引入 Xunit.StaFact 之类的包：手写一个线程就够了，少一个依赖就少一个
/// 在 CI 上还原失败的理由。代价是每个用例要自己包一层 <see cref="Run"/>。
/// </summary>
internal static class Sta
{
    internal static void Run(Action action)
    {
        ExceptionDispatchInfo? captured = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ExceptionDispatchInfo.Capture(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        // 用 ExceptionDispatchInfo 而不是 throw ex：保住原始堆栈。
        // 这个工程存在的意义就是把窗口构造期的堆栈打出来，
        // 在这里把它截断等于白做。
        captured?.Throw();
    }

    /// <summary>
    /// 显示窗口，并且<b>等它真的画出来</b>。
    ///
    /// 只调 Show() 是不够的：WPF 的渲染发生在 Dispatcher 队列被排空之后，
    /// 而且 DWM 合成还要再晚一点。截屏类的用例如果不等这一下，
    /// 拿到的会是一张还没有这个窗口的图——然后「没找到」会被误读成
    /// 「排除生效了」，一个假绿。
    /// </summary>
    internal static void ShowAndRender(System.Windows.Window window)
    {
        window.Show();
        Settle(window);
    }

    /// <summary>把队列排空并给合成器留出时间。改过窗口属性之后也要调一次。</summary>
    internal static void Settle(System.Windows.Window window)
    {
        for (int i = 0; i < 12; i++)
        {
            // ApplicationIdle 比 Render 更靠后，返回时这一帧已经交给合成器了
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Thread.Sleep(40);
        }
    }
}
