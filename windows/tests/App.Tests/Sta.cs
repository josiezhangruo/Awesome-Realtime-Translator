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
}
