using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using AwesomeRealtimeTranslator.Core;
using Whisper.net;

namespace AwesomeRealtimeTranslator.Transcription;

/// <summary>
/// 基于 whisper.cpp 的流式识别器，对应 macOS 版的 SpeechAnalyzerTranscriber。
///
/// <b>这是整个 Windows 版与 Mac 版差别最大的一块，必须说清楚。</b>
///
/// macOS 26 的 SpeechAnalyzer 本身就是流式的：喂音频进去，它自己吐 volatile 和
/// finalized 事件。Windows 没有对等的系统 API——Win11 的实时字幕是系统功能、
/// 不对外开放；旧的 System.Speech 是命令词识别，做不了自由听写。所以这里用
/// whisper.cpp，而 Whisper 是<b>非流式</b>的：它一次吃一整段音频。
///
/// 于是流式语义得自己造，做法是「滑动窗口 + 重复解码」：
///
/// <list type="number">
/// <item>把上次定稿之后的音频攒在窗口里。</item>
/// <item>每 <see cref="StepInterval"/> 把<b>整个窗口</b>重新解码一次，结果作为 Volatile 发出去。
///       所以同一段音频会被反复解码好几遍——这是 CPU 换「边说边出字」。</item>
/// <item>检测到对方停顿超过 <see cref="SilenceGap"/>，再解码一次，作为 Finalized 发出去并清空窗口。</item>
/// <item>解码结果里出现<b>句末标点</b>且前缀稳定，就把前缀定稿、窗口裁到那之后（见 <see cref="FinalizePolicy"/>）。</item>
/// <item>窗口超过 <see cref="MaxWindowSeconds"/> 也强制定稿，否则解码耗时会随窗口线性增长。</item>
/// </list>
///
/// 第三条是 0.2.1 加的。只有前两条的时候，真实会议里对方连着讲、没有 600ms 空档，
/// 唯一生效的触发器就剩 18 秒的窗口上限——实测译文连续 8 秒一个字不动，
/// 然后几句一起砸出来。定稿规则的全部判断已经抽进 <see cref="FinalizePolicy"/>，
/// 那是纯逻辑、有单测的，这里只负责把音频和事件接上去。
///
/// 静音判定一律按<b>挂钟时间</b>算，不靠数静音采样点：WASAPI loopback 在系统完全
/// 没声音时根本不给缓冲区（见 WasapiLoopbackSource），数采样点会永远数不满。
/// </summary>
public sealed class WhisperTranscriber : ITranscriber
{
    /// <summary>多久重新解码一次窗口，决定「边说边出字」的刷新率。</summary>
    private static readonly TimeSpan StepInterval = TimeSpan.FromMilliseconds(700);

    /// <summary>对方停顿多久算一句说完。和 Segmenter 的 0.45s 配合，这里取稍长一点。</summary>
    private static readonly TimeSpan SilenceGap = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// 窗口上限。超过就强制定稿，防止解码耗时失控。
    ///
    /// 0.2.1 从 18 秒降到 7 秒。18 秒当初是按「解码耗时」选的，没考虑到它同时也是
    /// 连续说话时<b>译文的最坏等待时间</b>——面试里 18 秒的空白等于整个问题讲完了
    /// 还没出一个中文字。而且 18 秒的窗口恰好是解码最慢的窗口，偏偏在你已经落后
    /// 最多的时候才到。
    ///
    /// 代价是跨窗口边界的长句会被劈开，译文质量掉一截。加了标点定稿之后这条
    /// 退化成几乎不触发的兜底，但真触发时确实会切坏——这是明知的取舍：
    /// 宁可偶尔切坏一句，不要偶尔等 18 秒。
    /// </summary>
    private const double MaxWindowSeconds = 7.0;

    /// <summary>低于这个时长的窗口不值得解码，纯属浪费 CPU。</summary>
    private const double MinWindowSeconds = 0.4;

    /// <summary>RMS 超过这个值算「有人在说话」。经验值，对应约 -46dBFS。</summary>
    private const double SpeechRms = 0.005;

    private readonly string _modelPath;
    private readonly string _language;
    private readonly int _threads;
    private readonly object _gate = new();
    private readonly List<float> _window = [];

    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private long _lastSpeechTicks;
    private bool _windowHasSpeech;

    private readonly FinalizePolicy _policy =
        new(SilenceGap.TotalSeconds, MaxWindowSeconds);

    public WhisperTranscriber(string modelPath, string language, int? threads = null)
    {
        _modelPath = modelPath;
        _language = language;
        _threads = threads ?? Math.Clamp(Environment.ProcessorCount - 2, 2, 8);
    }

    public event Action<TranscriptEvent>? Event;

    // 必须是 async：原来写成同步方法 + try 包住 Task.Run，而 Task.Run 是立刻返回的，
    // 真正的异常在 await 的时候才浮出来，根本不经过那个 catch。于是模型加载失败时
    // 抛出去的是 Whisper.net 的原始异常，既没有上下文也不告诉用户该怎么办。
    public async Task PrepareAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_modelPath))
        {
            throw new TranscriberException(
                $"找不到语音识别模型：{_modelPath}\n" +
                "检查一下 models 文件夹是不是和 exe 放在一起了。");
        }

        long size = new FileInfo(_modelPath).Length;
        if (size < MinPlausibleModelBytes)
        {
            // 下载中断或解压失败留下的半截文件。不提前拦的话，
            // 用户会看到一句来自 native 层、完全看不懂的错误。
            throw new TranscriberException(
                $"语音识别模型只有 {size / 1024 / 1024} MB，不完整（正常约 142 MB）。\n" +
                "多半是下载或解压中断了，重新解压一次安装包。");
        }

        // 大小对得上、内容不对的情况是真会发生的：解压中断、杀毒软件替换过文件、
        // 外接盘没写完就拔掉。不先验这一下，whisper.cpp 会在 native 层直接踩空，
        // 抛一个 SEHException，用户只看到「External component has thrown an exception」。
        // 不用 stackalloc：这是 async 方法，栈上的 Span 跨不过下面的 await。
        byte[] header = new byte[4];
        using (FileStream stream = File.OpenRead(_modelPath))
        {
            stream.ReadExactly(header, 0, header.Length);
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != GgmlMagic)
        {
            throw new TranscriberException(
                $"语音识别模型文件损坏了：{_modelPath}\n" +
                "大小对得上，但文件头不对，内容是坏的。\n" +
                "重新完整解压一次安装包，并且把程序文件夹放在本机硬盘上，别放外接盘或网络盘。");
        }

        progress?.Report("正在加载语音识别模型…");

        try
        {
            // 加载是 CPU 密集的同步调用（base 模型约 1~3 秒），放到线程池上，
            // 否则会把 UI 线程卡住，用户会以为点了没反应。
            await Task.Run(
                () =>
                {
                    WhisperFactory factory = WhisperFactory.FromPath(_modelPath);
                    WhisperProcessor processor = factory.CreateBuilder()
                        .WithLanguage(_language)
                        .WithThreads(_threads)
                        // 每一步都是对同一段音频重新解码，不能让上一步的结果当上下文，
                        // 否则模型会越跑越偏，出现滚雪球式的幻觉。
                        .WithNoContext()
                        // 温度 0：同一段音频每次解码结果必须一致。
                        // 不一致的话屏幕上的字会自己改来改去，比延迟难受得多。
                        .WithTemperature(0f)
                        .WithNoSpeechThreshold(0.6f)
                        .Build();

                    lock (_gate)
                    {
                        _factory = factory;
                        _processor = processor;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DllNotFoundException ex)
        {
            // 0.1.2 起原生库就放在 exe 旁边的 runtimes\<架构>\ 里，不再自解压到临时目录，
            // 所以到这里只剩两种可能，而且能当场分辨：目录在不在。
            // （0.1.2 的提示文字还在说「杀软拦截了解压出来的组件」，那是 0.1.1 的世界，
            //   已经不成立了，会把人引到错误的方向上去。）
            string runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
            string hint = Directory.Exists(runtimes)
                ? "组件文件夹是在的，那多半是杀毒软件把里面的 dll 隔离了。\n" +
                  "把整个程序文件夹加入杀毒软件的信任列表，然后重试。"
                : $"找不到 {runtimes}。\n" +
                  "整个文件夹要保持原样，不能只把 exe 单独拖出来——重新完整解压一次安装包。";

            throw new TranscriberException(
                $"语音识别组件加载失败，系统找不到它需要的原生库。\n{hint}\n（{ex.Message}）",
                ex);
        }
        catch (SEHException ex)
        {
            // 原生层踩空了，而大小和文件头都已经验过，所以不是明显的坏文件。
            //
            // 线上见过一次这个，证据是：程序放在 E: 盘时连续两次都在这里失败，
            // 整个文件夹复制到 C:\Users\...\Downloads 下之后连续三次都正常。
            // 真正的原因没查清（没有 Windows 机器可复现），但那条经验对用户有用，
            // 比丢一句「External component has thrown an exception」强。
            throw new TranscriberException(
                "语音识别引擎在加载模型时崩了，但模型文件本身看起来是完整的。\n" +
                "已知有效的办法：把整个程序文件夹复制到系统盘上再运行——\n" +
                "比如「下载」或「文档」里，别放在其他分区、外接硬盘、U 盘或网络盘上。\n" +
                $"（{ex.Message}）",
                ex);
        }
        catch (Exception ex) when (ex is not TranscriberException)
        {
            throw new TranscriberException($"语音识别模型加载失败：{ex.Message}", ex);
        }
    }

    /// <summary>比这还小的模型文件一定是坏的。最小的 tiny 模型也有 74MB。</summary>
    private const long MinPlausibleModelBytes = 60L * 1024 * 1024;

    /// <summary>
    /// ggml 文件头的前 4 字节，GGML_FILE_MAGIC。落到磁盘上是 ASCII "lmgg"
    /// （小端），已经对着实际的 ggml-base.bin 用 xxd 核过。
    /// </summary>
    private const uint GgmlMagic = 0x67676d6c;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_processor is null)
            {
                throw new TranscriberException("识别器还没准备好，请先调用 PrepareAsync。");
            }

            _window.Clear();
            _windowHasSpeech = false;
            _policy.Reset();
            _lastSpeechTicks = Stopwatch.GetTimestamp();
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public void Append(AudioChunk chunk)
    {
        double rms = Rms(chunk.Samples);

        lock (_gate)
        {
            _window.AddRange(chunk.Samples);
            if (rms >= SpeechRms)
            {
                _windowHasSpeech = true;
                _lastSpeechTicks = Stopwatch.GetTimestamp();
            }
        }
    }

    private static double Rms(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (float s in samples)
        {
            sum += (double)s * s;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(StepInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            float[] snapshot;
            bool hasSpeech;
            double silentFor;

            lock (_gate)
            {
                snapshot = [.. _window];
                hasSpeech = _windowHasSpeech;
                silentFor = Stopwatch.GetElapsedTime(_lastSpeechTicks).TotalSeconds;
            }

            double seconds = snapshot.Length / (double)AudioChunk.SampleRate;
            if (!hasSpeech || seconds < MinWindowSeconds)
            {
                continue;
            }

            IReadOnlyList<DecodedSegment> segments;
            try
            {
                segments = await DecodeAsync(snapshot, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Event?.Invoke(TranscriptEvent.Failed(ex.Message));
                continue;
            }

            FinalizeDecision decision = _policy.Evaluate(segments, silentFor, seconds);

            if (!decision.ShouldFinalize)
            {
                // 这里<b>不</b>做「文本没变就不发」的去重。去重看着省事，实际上
                // 让下游的静音判定失灵：Segmenter 数的是「距离上一个事件多久」，
                // 解码结果稳定时不发事件，就等于告诉它「对方停下来了」，
                // 于是半句话在随机位置被定稿。重复事件的成本在 CaptionStore
                // 的 setter 里已经挡掉了（值相同不触发 PropertyChanged）。
                Event?.Invoke(TranscriptEvent.Volatile(Join(segments, segments.Count)));
                continue;
            }

            lock (_gate)
            {
                int consumed = decision.DropAll
                    // 只丢掉已经解码过的部分：解码期间可能又进来了新音频，
                    // 那部分必须留给下一个窗口，否则对方说话快的时候会吞字。
                    ? Math.Min(snapshot.Length, _window.Count)
                    : Math.Min(
                        (int)(decision.TrimSeconds * AudioChunk.SampleRate),
                        Math.Min(snapshot.Length, _window.Count));

                if (consumed > 0)
                {
                    _window.RemoveRange(0, consumed);
                }

                // 整窗退休了才算没声音；裁掉一截之后窗口里剩下的仍然是人声，
                // 标成 false 会让下一轮直接 continue，白白丢掉半句。
                if (decision.DropAll)
                {
                    _windowHasSpeech = false;
                }
            }

            if (decision.Text.Length > 0)
            {
                Event?.Invoke(TranscriptEvent.Finalized(decision.Text));
            }

            // 部分定稿时，剩下的尾巴立刻补一个 volatile，不然要等下一个
            // StepInterval 原文区才跟上，屏幕上会空一拍。
            if (!decision.DropAll)
            {
                string whole = Join(segments, segments.Count);
                string tail = whole.StartsWith(decision.Text, StringComparison.Ordinal)
                    ? whole[decision.Text.Length..].TrimStart()
                    : string.Empty;
                if (tail.Length > 0)
                {
                    Event?.Invoke(TranscriptEvent.Volatile(tail));
                }
            }
        }
    }

    private async Task<IReadOnlyList<DecodedSegment>> DecodeAsync(float[] samples, CancellationToken token)
    {
        WhisperProcessor? processor;
        lock (_gate)
        {
            processor = _processor;
        }

        if (processor is null)
        {
            return [];
        }

        var parts = new List<DecodedSegment>();
        await foreach (SegmentData segment in processor.ProcessAsync(samples, token).ConfigureAwait(false))
        {
            // 这里只剥标记不 Trim：段与段之间的空格是拼接的唯一依据，
            // 提前 Trim 掉会把 "and" "then" 粘成 "andthen"。
            parts.Add(new DecodedSegment(StripMarkers(segment.Text), segment.End.TotalSeconds));
        }

        return parts;
    }

    /// <summary>把若干段拼成一句话，和 <see cref="FinalizePolicy"/> 里的拼法保持一致。</summary>
    private static string Join(IReadOnlyList<DecodedSegment> segments, int count)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count && i < segments.Count; i++)
        {
            sb.Append(segments[i].Text);
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// whisper.cpp 会把非语音事件写成 <c>[BLANK_AUDIO]</c>、<c>(music)</c> 这类标记，
    /// 直接显示到字幕上很难看，在这里清掉。
    ///
    /// <b>刻意不 Trim。</b>whisper 在每段前面加一个空格，那个空格是段与段之间
    /// 唯一的分词依据；按段清理时提前 Trim 掉，拼起来就成了 <c>andthen</c>。
    /// 首尾空白统一由拼接方（<see cref="Join"/> / <see cref="FinalizePolicy"/>）去掉。
    /// </summary>
    private static string StripMarkers(string raw)
    {
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = raw.AsSpan();
        var sb = new System.Text.StringBuilder(span.Length);
        int depth = 0;

        foreach (char c in span)
        {
            switch (c)
            {
                case '[' or '(':
                    depth++;
                    break;
                case ']' or ')':
                    if (depth > 0)
                    {
                        depth--;
                    }

                    break;
                default:
                    if (depth == 0)
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.ToString();
    }

    public async Task FinishAsync()
    {
        CancellationTokenSource? cts = _cts;
        Task? loop = _loop;
        _cts = null;
        _loop = null;

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停机
            }
        }

        cts?.Dispose();

        // 窗口里可能还剩最后一句没定稿的话，解码一次发出去，不能吞掉。
        float[] remaining;
        bool hasSpeech;
        lock (_gate)
        {
            remaining = [.. _window];
            hasSpeech = _windowHasSpeech;
            _window.Clear();
            _windowHasSpeech = false;
        }

        if (!hasSpeech || remaining.Length / (double)AudioChunk.SampleRate < MinWindowSeconds)
        {
            return;
        }

        try
        {
            IReadOnlyList<DecodedSegment> segments =
                await DecodeAsync(remaining, CancellationToken.None).ConfigureAwait(false);
            string text = Join(segments, segments.Count);
            if (text.Length > 0)
            {
                Event?.Invoke(TranscriptEvent.Finalized(text));
            }
        }
        catch
        {
            // 停机路径上解码失败无所谓，不值得打扰用户
        }
    }

    public async ValueTask DisposeAsync()
    {
        await FinishAsync().ConfigureAwait(false);

        WhisperProcessor? processor;
        WhisperFactory? factory;
        lock (_gate)
        {
            processor = _processor;
            factory = _factory;
            _processor = null;
            _factory = null;
        }

        if (processor is not null)
        {
            await processor.DisposeAsync().ConfigureAwait(false);
        }

        factory?.Dispose();
    }
}
