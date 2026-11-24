using System.Threading.Channels;

namespace AwesomeRealtimeTranslator.Core;

public enum PipelineState
{
    Idle,
    Preparing,
    Running,
    Paused,
    Failed,
}

/// <summary>
/// 整条管道的装配与生命周期：
///
/// <code>
/// WasapiLoopbackSource ──PCM──► WhisperTranscriber
///                                     │ TranscriptEvent
///                                     ▼
///                                 Segmenter ──Sentence──► 翻译串行队列 ──► CaptionStore
/// </code>
///
/// 几个刻意的设计（和 macOS 版一致）：
/// <list type="bullet">
/// <item>翻译走<b>串行</b>队列：保证译文顺序与原文一致，也避免并发请求把句子插乱。</item>
/// <item>翻译引擎准备失败不会让整条管道挂掉，降级成「仅原文」并在字幕区给一行提示。</item>
/// <item>所有对 <see cref="CaptionStore"/> 的写入都回到 UI 线程，UI 不需要额外同步。</item>
/// </list>
///
/// 平台相关的三样东西（音频源、识别器、翻译器）都是构造注入的接口，
/// 所以这个类本身是平台无关的，可以用假实现完整单测。
/// </summary>
public sealed class SessionPipeline : IAsyncDisposable
{
    private readonly CaptionStore _store;
    private readonly AppSettings _settings;
    private readonly Func<IAudioSource> _audioFactory;
    private readonly Func<ITranscriber> _transcriberFactory;
    private readonly Func<ITranslator> _translatorFactory;
    private readonly Action<Action> _toUiThread;
    private readonly TimeProvider _time;

    /// <summary>
    /// 失败日志的出口。Core 不认识任何具体的日志实现，由 App 注入。
    /// 不注入就什么也不记——单测用的就是这条路径。
    /// </summary>
    private readonly Action<string, Exception?> _log;

    private IAudioSource? _audio;
    private ITranscriber? _transcriber;
    private ITranslator? _translator;
    private bool _usesRealTranslator;
    private Segmenter _segmenter = new();

    private Channel<Sentence>? _translationChannel;
    private Task? _translationPump;
    private CancellationTokenSource? _cts;
    private Task? _ticker;
    private long _sessionStartTicks;

    public SessionPipeline(
        CaptionStore store,
        AppSettings settings,
        Func<IAudioSource> audioFactory,
        Func<ITranscriber> transcriberFactory,
        Func<ITranslator> translatorFactory,
        Action<Action> toUiThread,
        TimeProvider? time = null,
        Action<string, Exception?>? log = null)
    {
        _store = store;
        _settings = settings;
        _audioFactory = audioFactory;
        _transcriberFactory = transcriberFactory;
        _translatorFactory = translatorFactory;
        _toUiThread = toUiThread;
        _time = time ?? TimeProvider.System;
        _log = log ?? ((_, _) => { });
    }

    public PipelineState State { get; private set; } = PipelineState.Idle;

    public string StatusLabel => State switch
    {
        PipelineState.Idle => "未开始",
        PipelineState.Preparing => "准备中…",
        PipelineState.Running => "正在听",
        PipelineState.Paused => "已暂停",
        _ => "出错",
    };

    public event Action? StateChanged;

    public bool IsActive => State is PipelineState.Running or PipelineState.Paused or PipelineState.Preparing;

    // 用注入的 TimeProvider 自己的频率，不是 TimeProvider.System 的——
    // 测试里换成假时钟时，两者的频率可以不一样。
    private double Now => (_time.GetTimestamp() - _sessionStartTicks) / (double)_time.TimestampFrequency;

    public async Task StartAsync()
    {
        if (IsActive)
        {
            return;
        }

        SetState(PipelineState.Preparing);
        _sessionStartTicks = _time.GetTimestamp();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        _store.StartSession();
        _store.IsRunning = false;
        _store.StatusMessage = null;

        _segmenter = new Segmenter(new Segmenter.Configuration
        {
            // 1.4s = 识别器 StepInterval(700ms) 的两倍。这里有一条隐式耦合：
            // Segmenter 的静音判定看的是「距离上一个事件多久」，而识别器在
            // 对方说话时每 700ms 发一个 volatile。所以任何小于 0.7 的取值，
            // 测的都不是对方停顿，而是识别器的解码节拍——每两个 volatile 之间
            // 都会触发一次定稿，把句子切在随机位置上。
            //
            // 0.2.0 这里是 0.45（本意是让译文早点出来），用户录像里那句
            // 切在 "and I" 上的残片就是它干的。见 SilenceCouplingTests。
            //
            // 真正的「一句说完了」由识别器自己的 SilenceGap(600ms) 判断，它先触发；
            // 这里只是识别器彻底不吐东西时的兜底，不需要灵敏。
            SilenceFlushInterval = 1.4,
        });

        // 1. 识别器
        ITranscriber transcriber = _transcriberFactory();
        transcriber.Event += OnTranscriptEvent;
        try
        {
            await transcriber.PrepareAsync(
                new Progress<string>(m => _toUiThread(() => _store.StatusMessage = m)),
                token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("识别器准备失败", ex);
            transcriber.Event -= OnTranscriptEvent;
            await transcriber.DisposeAsync().ConfigureAwait(false);
            Fail(ex.Message);
            return;
        }

        _transcriber = transcriber;

        // 2. 翻译引擎（失败降级，不阻断原文字幕）
        ITranslator translator = _translatorFactory();
        try
        {
            await translator.PrepareAsync(
                new Progress<string>(m => _toUiThread(() => _store.StatusMessage = m)),
                token).ConfigureAwait(false);
            _translator = translator;
            _usesRealTranslator = true;
            _toUiThread(() => _store.StatusMessage = null);
        }
        catch (Exception ex)
        {
            _log("翻译引擎降级为仅原文", ex);
            await translator.DisposeAsync().ConfigureAwait(false);
            _translator = new PassthroughTranslator();
            _usesRealTranslator = false;
            string hint = (ex as TranslatorException)?.RecoverySuggestion is { } s ? "\n" + s : string.Empty;
            _toUiThread(() => _store.StatusMessage = $"只显示原文：{ex.Message}{hint}");
        }

        // 3. 管道（先订阅，后喂数据，避免丢前几个事件）
        StartTranslationPump(token);
        StartTicker(token);

        try
        {
            await transcriber.StartAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("识别器启动失败", ex);
            Fail(ex.Message);
            await TeardownAsync().ConfigureAwait(false);
            return;
        }

        // 4. 音频采集
        IAudioSource audio = _audioFactory();
        audio.ChunkAvailable += OnChunk;
        audio.Failed += OnAudioFailed;
        try
        {
            await audio.StartAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log("音频采集启动失败", ex);
            audio.ChunkAvailable -= OnChunk;
            audio.Failed -= OnAudioFailed;
            await audio.DisposeAsync().ConfigureAwait(false);
            Fail(ex.Message);
            await TeardownAsync().ConfigureAwait(false);
            return;
        }

        _audio = audio;
        SetState(PipelineState.Running);
        _toUiThread(() => _store.IsRunning = true);
    }

    public async Task PauseAsync()
    {
        if (State != PipelineState.Running)
        {
            return;
        }

        IAudioSource? audio = _audio;
        _audio = null;
        SetState(PipelineState.Paused);
        _toUiThread(() => _store.IsRunning = false);

        if (audio is not null)
        {
            audio.ChunkAvailable -= OnChunk;
            audio.Failed -= OnAudioFailed;
            await audio.StopAsync().ConfigureAwait(false);
            await audio.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task ResumeAsync()
    {
        if (State != PipelineState.Paused || _transcriber is null || _cts is null)
        {
            return;
        }

        IAudioSource audio = _audioFactory();
        audio.ChunkAvailable += OnChunk;
        audio.Failed += OnAudioFailed;
        try
        {
            await audio.StartAsync(_cts.Token).ConfigureAwait(false);
            _audio = audio;
            SetState(PipelineState.Running);
            _toUiThread(() => _store.IsRunning = true);
        }
        catch (Exception ex)
        {
            audio.ChunkAvailable -= OnChunk;
            audio.Failed -= OnAudioFailed;
            await audio.DisposeAsync().ConfigureAwait(false);
            Fail(ex.Message);
        }
    }

    public async Task StopAsync()
    {
        if (!IsActive)
        {
            return;
        }

        // 冲刷出最后一句
        foreach (Sentence sentence in _segmenter.Flush(Now))
        {
            Emit(sentence);
        }

        _toUiThread(() => _store.PendingSource = string.Empty);

        await TeardownAsync().ConfigureAwait(false);
        SetState(PipelineState.Idle);
        _toUiThread(() => _store.IsRunning = false);
    }

    public async Task ToggleAsync()
    {
        switch (State)
        {
            case PipelineState.Running:
                await PauseAsync().ConfigureAwait(false);
                break;
            case PipelineState.Paused:
                await ResumeAsync().ConfigureAwait(false);
                break;
            case PipelineState.Idle:
            case PipelineState.Failed:
                await StartAsync().ConfigureAwait(false);
                break;
            default:
                break;
        }
    }

    // MARK: - 管道内部

    private void OnChunk(AudioChunk chunk) => _transcriber?.Append(chunk);

    // 带上下文的消息整句传给 Fail，别自己先写一遍 StatusMessage——
    // 那样两处都在管同一个字段，很容易再次写出互相覆盖的 bug。
    private void OnAudioFailed(Exception ex)
    {
        _log("音频采集中断", ex);
        Fail($"音频采集中断：{ex.Message}");
    }

    private void OnTranscriptEvent(TranscriptEvent evt)
    {
        if (evt.Kind == TranscriptEventKind.Failed)
        {
            _toUiThread(() => _store.StatusMessage = $"识别中断：{evt.Text}");
            return;
        }

        double now = Now;
        IReadOnlyList<Sentence> sentences = _segmenter.Ingest(evt, now);
        string pending = _segmenter.PendingText;

        _toUiThread(() =>
        {
            _store.PendingSource = pending;
            foreach (Sentence sentence in sentences)
            {
                EmitOnUiThread(sentence);
            }
        });
    }

    /// <summary>静音定稿需要一个时钟。100ms 的粒度对 450ms 的阈值足够，开销可忽略。</summary>
    private void StartTicker(CancellationToken token) => _ticker = Task.Run(
        async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                IReadOnlyList<Sentence> sentences = _segmenter.Tick(Now);
                if (sentences.Count == 0)
                {
                    continue;
                }

                string pending = _segmenter.PendingText;
                _toUiThread(() =>
                {
                    foreach (Sentence sentence in sentences)
                    {
                        EmitOnUiThread(sentence);
                    }

                    _store.PendingSource = pending;
                });
            }
        },
        token);

    private void StartTranslationPump(CancellationToken token)
    {
        _translationChannel = Channel.CreateUnbounded<Sentence>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });

        ChannelReader<Sentence> reader = _translationChannel.Reader;
        _translationPump = Task.Run(
            async () =>
            {
                await foreach (Sentence sentence in reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    if (!_usesRealTranslator || _translator is null)
                    {
                        continue;
                    }

                    IReadOnlyList<string> context = _store.RecentContext(3);
                    try
                    {
                        // 边解码边往字幕里填前缀。贪心解码是 append-only 的，
                        // 后一次一定以前一次为前缀，所以这不构成「译文被改写」。
                        Sentence current = sentence;
                        var partial = new SyncProgress<string>(
                            prefix => _toUiThread(() => _store.SetTranslation(prefix, current.Id)));

                        string translated = await _translator
                            .TranslateAsync(sentence.Text, context, partial, token)
                            .ConfigureAwait(false);
                        _toUiThread(() => _store.SetTranslation(translated, sentence.Id));
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _log("单句翻译失败", ex);
                        _toUiThread(() => _store.MarkTranslationFailed(sentence.Id));
                    }
                }
            },
            token);
    }

    private void Emit(Sentence sentence) => _toUiThread(() => EmitOnUiThread(sentence));

    private void EmitOnUiThread(Sentence sentence)
    {
        _store.Append(sentence);
        _translationChannel?.Writer.TryWrite(sentence);
    }

    private async Task TeardownAsync()
    {
        IAudioSource? audio = _audio;
        _audio = null;
        if (audio is not null)
        {
            audio.ChunkAvailable -= OnChunk;
            audio.Failed -= OnAudioFailed;
            await audio.StopAsync().ConfigureAwait(false);
            await audio.DisposeAsync().ConfigureAwait(false);
        }

        if (_transcriber is not null)
        {
            // 留一点时间让最后的 finalized 结果流出来
            await _transcriber.FinishAsync().ConfigureAwait(false);
            _transcriber.Event -= OnTranscriptEvent;
            await _transcriber.DisposeAsync().ConfigureAwait(false);
            _transcriber = null;
        }

        _translationChannel?.Writer.TryComplete();
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        await SafeAwait(_ticker).ConfigureAwait(false);
        await SafeAwait(_translationPump).ConfigureAwait(false);
        _ticker = null;
        _translationPump = null;
        _translationChannel = null;

        if (_translator is not null)
        {
            await _translator.DisposeAsync().ConfigureAwait(false);
            _translator = null;
        }

        _usesRealTranslator = false;
        _cts?.Dispose();
        _cts = null;
    }

    private static async Task SafeAwait(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停机路径
        }
    }

    /// <summary>
    /// 会话失败：把原因显示给用户，并切到 Failed。
    ///
    /// 这里<b>必须覆盖</b> StatusMessage，不能用 <c>??=</c>。
    /// 原来写的是 <c>??=</c>，而准备阶段的进度回调早就把它写成了
    /// 「正在加载语音识别模型…」，于是真正的错误一个字都显示不出来——
    /// 线上反馈的「一直卡在加载中、不报错也不翻译」就是这么来的。
    ///
    /// 想带上下文的调用方，把修饰好的整句话传进来，不要自己先写一遍 StatusMessage。
    /// </summary>
    private void Fail(string message)
    {
        _toUiThread(() => _store.StatusMessage = message);
        SetState(PipelineState.Failed);
    }

    private void SetState(PipelineState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        _toUiThread(() => StateChanged?.Invoke());
    }

    public async ValueTask DisposeAsync() => await TeardownAsync().ConfigureAwait(false);
}
