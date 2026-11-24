namespace AwesomeRealtimeTranslator.Ui.Tests;

using System.Runtime.ExceptionServices;
using System.Windows.Threading;

/// <summary>
/// 所有 WPF 用例共用的那一条 UI 线程。
///
/// <b>为什么是「一条」而不是「每个用例一条」。</b>最初的写法是每次
/// <see cref="Run"/> 都开一条新的 STA 线程，结果在 CI 上炸了四个用例：
///
/// <code>
/// InvalidOperationException: Cannot use a DependencyObject that belongs to
/// a different thread than its parent Freezable.
///     at System.Windows.Freezable.EnsureConsistentDispatchers(...)
/// </code>
///
/// WPF 会把 BAML 里可共享的 Freezable（画刷、样式这些）缓存起来，而缓存项
/// 带着「第一个创建它的线程」的亲和性。于是第一个跑到的用例成功，后面所有
/// 在别的线程上创建同一个窗口的用例全部失败。
///
/// 这是测试写法的问题，不是产品的问题——真实应用只有一条 UI 线程，
/// 永远碰不到这个。但它恰好说明了一件事：<b>把测试环境搭得和真实运行环境不一样，
/// 测出来的失败就可能既不是真 bug、也不能证明没有真 bug。</b>
///
/// 现在改成：进程里只有一条 STA 线程，上面跑一个真正的 <see cref="Dispatcher"/>
/// 消息循环，所有用例都 Invoke 到它上面去。顺带还有两个好处——窗口能真的渲染
/// （截屏用例需要），并且用例之间天然串行（屏幕是全局状态，两个用例同时显示
/// 窗口会互相干扰）。
/// </summary>
internal static class Sta
{
    private static readonly Lazy<Dispatcher> UiThread = new(StartUiThread);

    private static Dispatcher StartUiThread()
    {
        var ready = new TaskCompletionSource<Dispatcher>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            // 先把本线程的 Dispatcher 交出去，再进消息循环。
            // 顺序反了就永远交不出去——Dispatcher.Run() 不会返回。
            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            Name = "wpf-test-ui",

            // 后台线程：测试跑完进程要能正常退出，而 Dispatcher.Run() 是个死循环
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return ready.Task.GetAwaiter().GetResult();
    }

    internal static void Run(Action action)
    {
        ExceptionDispatchInfo? captured = null;

        UiThread.Value.Invoke(() =>
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

        // 自己接再自己抛，而不是让异常穿过 Dispatcher.Invoke：那条路上
        // 异常可能被包一层，而这个工程存在的意义就是把原始堆栈打出来。
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
            // 这里已经在 UI 线程上了，所以要用 DoEvents 式的嵌套泵，
            // 不能再 Invoke 一次（那会死锁）。
            DoEvents(window.Dispatcher);
            Thread.Sleep(40);
        }
    }

    /// <summary>
    /// 在当前线程上把 Dispatcher 队列跑到 ApplicationIdle 为止。
    /// ApplicationIdle 比 Render 更靠后，排到它说明这一帧已经交给合成器了。
    /// </summary>
    private static void DoEvents(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
