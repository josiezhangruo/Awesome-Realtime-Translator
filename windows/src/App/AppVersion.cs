namespace AwesomeRealtimeTranslator;

/// <summary>
/// 当前版本号，界面、托盘和日志共用一个来源。
///
/// <b>为什么专门开一个。</b>0.1.5 之前版本号只出现在日志里，用户看不到自己装的
/// 是哪一版。而这个项目在两天里发了四个版本，每次排查的第一句话都是
/// 「你装的是哪个版本」——然后用户得去翻 %APPDATA% 里的 log.txt 才能回答。
///
/// 值来自 App.csproj 的 &lt;Version&gt;，不手写、不可能和实际产物对不上。
/// </summary>
internal static class AppVersion
{
    /// <summary>形如 <c>0.2.0</c>。取三段，省掉 MSBuild 自动补的那个 .0。</summary>
    internal static string Current { get; } =
        typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "未知";
}
