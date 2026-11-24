using Xunit;

// 这个程序集不并行跑用例。
//
// 两个理由：
// 1. 屏幕是全局状态。ScreenCaptureTests 要求那一刻只有它自己的窗口在最上层，
//    别的用例同时显示一个窗口就会把结果搅乱——而那种失败是随机出现的，
//    最难查的一类。
// 2. 所有 WPF 用例本来就共用一条 UI 线程（见 Sta），并行只会让它们在
//    Dispatcher 上排队，一点没快，却让「此刻屏幕上有什么」变得不可预测。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
