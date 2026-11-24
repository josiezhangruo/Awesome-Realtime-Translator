using System.Globalization;
using System.IO;
using System.Text;
using AwesomeRealtimeTranslator.Settings;

namespace AwesomeRealtimeTranslator.Diagnostics;

/// <summary>
/// 往 <c>%APPDATA%\AwesomeRealtimeTranslator\log.txt</c> 写一行一条的日志。
///
/// <b>为什么需要它。</b>第一个线上反馈是「一直卡在正在加载语音识别模型」，
/// 而这个应用当时一行日志都没有，用户除了描述现象什么也给不出来，
/// 我也没有 Windows 机器能复现。真正的失败原因（杀毒拦截？模型损坏？缺运行库？）
/// 只能靠猜。
///
/// 所以加这个。刻意不引日志框架：要的只是「出事时有一份能贴给我看的文本」。
/// 单文件 + 自带模型已经 389MB，不值得再为一行文本加个依赖。
///
/// 隐私：只记录程序自己的状态——版本、路径、文件大小、异常。
/// <b>绝不记录任何识别出来的文字或译文</b>，那是面试内容。
/// </summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;

    private static readonly object Gate = new();
    private static readonly string Path = System.IO.Path.Combine(
        FileSettingsStore.DefaultDirectory, "log.txt");

    public static string FilePath => Path;

    public static string Directory => FileSettingsStore.DefaultDirectory;

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
    {
        if (ex is null)
        {
            Write("ERROR", message);
            return;
        }

        var sb = new StringBuilder(message);
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            sb.Append("\n    ").Append(e.GetType().Name).Append(": ").Append(e.Message);
        }

        if (ex.StackTrace is { } stack)
        {
            sb.Append("\n    ").Append(stack.Replace("\n", "\n    "));
        }

        Write("ERROR", sb.ToString());
    }

    /// <summary>
    /// 启动时记一遍环境。排查「在我这儿好好的」类问题全靠这几行：
    /// 系统版本够不够、模型在不在、大小对不对。
    /// </summary>
    public static void WriteStartupBanner(string? whisperModel, string? translationModel)
    {
        Write("INFO ", new StringBuilder()
            .Append("启动 ").Append(AppVersion.Current)
            .Append("\n    OS: ").Append(Environment.OSVersion.VersionString)
            .Append("  64 位: ").Append(Environment.Is64BitOperatingSystem)
            .Append("  CPU 核: ").Append(Environment.ProcessorCount)
            .Append("\n    程序目录: ").Append(AppContext.BaseDirectory)
            .Append("\n    所在磁盘: ").Append(DescribeDrive(AppContext.BaseDirectory))
            .Append("\n    识别模型: ").Append(Describe(whisperModel))
            .Append("\n    翻译模型: ").Append(Describe(translationModel))
            .ToString());
    }

    /// <summary>
    /// 记一下程序装在什么盘上。
    ///
    /// <b>这行是为一个没查清的 bug 留的证据。</b>0.1.3 的线上日志里，程序放在
    /// E: 盘时 whisper 加载模型连续两次抛 SEHException，整个文件夹复制到 C: 盘
    /// 之后连续三次都正常。病因至今不明，而我没有 Windows 机器可以复现。
    ///
    /// 那就至少把盘的类型和文件系统记下来——下一次再有人踩到，就有东西可比：
    /// 是不是都发生在可移动盘上？是不是都不是 NTFS？没有这一行，下一份日志
    /// 还是只能靠猜，而这个项目已经为「靠猜」付过四个版本的代价了。
    /// </summary>
    private static string DescribeDrive(string path)
    {
        try
        {
            string? root = System.IO.Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
            {
                return "读不出盘符";
            }

            var drive = new DriveInfo(root);

            // DriveFormat 在盘未就绪时会抛，所以它单独放在最后、也在 try 里。
            return $"{drive.Name} 类型 {drive.DriveType} 文件系统 {drive.DriveFormat}";
        }
        catch (Exception ex)
        {
            return $"读不出来（{ex.GetType().Name}）";
        }
    }

    private static string Describe(string? path)
    {
        if (path is null)
        {
            return "没找到";
        }

        try
        {
            if (File.Exists(path))
            {
                return $"{path}（{new FileInfo(path).Length / 1024 / 1024} MB）";
            }

            if (System.IO.Directory.Exists(path))
            {
                long total = new DirectoryInfo(path)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
                return $"{path}（{total / 1024 / 1024} MB）";
            }
        }
        catch
        {
            // 量不出来就算了，不能因为写日志把启动搞挂
        }

        return path;
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(FileSettingsStore.DefaultDirectory);
                Rotate();

                string line = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}\n");
                File.AppendAllText(Path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 写日志失败绝不能影响程序本身——日志是辅助，不是功能
        }
    }

    /// <summary>超过上限就丢掉旧的那一半，长会话跑一天也不会把磁盘写满。</summary>
    private static void Rotate()
    {
        try
        {
            var info = new FileInfo(Path);
            if (!info.Exists || info.Length <= MaxBytes)
            {
                return;
            }

            string[] lines = File.ReadAllLines(Path);
            File.WriteAllLines(Path, lines.Skip(lines.Length / 2), Encoding.UTF8);
        }
        catch
        {
            // 轮转失败就让它继续长，比丢日志强
        }
    }
}
