using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeRealtimeTranslator.Core;

namespace AwesomeRealtimeTranslator.Settings;

/// <summary>
/// 设置落盘到 <c>%APPDATA%\AwesomeRealtimeTranslator\settings.json</c>，
/// 对应 macOS 版的 UserDefaults。
///
/// 用 JSON 而不是注册表：用户能自己看、自己改、自己删，卸载时删一个目录就干净了。
/// </summary>
public sealed class FileSettingsStore : ISettingsStore, IDisposable
{
    /// <summary>停手多久之后才真的落盘。见 <see cref="Set"/> 里的解释。</summary>
    private const int SaveDelayMs = 300;

    private readonly string _path;
    private readonly JsonObject _root;
    private readonly object _gate = new();
    private readonly Timer _saveTimer;
    private bool _dirty;

    public FileSettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        _root = Load(_path);
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AwesomeRealtimeTranslator");

    public static string DefaultPath => Path.Combine(DefaultDirectory, "settings.json");

    private static JsonObject Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        }
        catch
        {
            // 文件损坏时宁可用默认值重新开始，也不能因为设置读不了就启动不了。
            return [];
        }
    }

    // 读也要进锁。0.1.4 给这个类加了延迟落盘的 Timer，于是 _root 从此会被
    // 线程池上的回调碰到（Flush → Save → ToJsonString）。JsonObject 不是
    // 线程安全的，读的同时有人写就是真的数据竞争。
    //
    // 现在实际上撞不上：AppSettings 只在构造时读一次，那时还没有人 Set 过。
    // 但这是「现在恰好安全」，不是「设计上安全」——加锁的代价是零，
    // 而这个项目已经为「看起来没问题」付过足够多的学费了。
    public string? GetString(string key)
    {
        lock (_gate)
        {
            return _root[key]?.GetValue<string>();
        }
    }

    public double? GetDouble(string key) => TryGet<double>(key);

    public int? GetInt(string key) => TryGet<int>(key);

    public bool? GetBool(string key) => TryGet<bool>(key);

    public IReadOnlyList<string>? GetStringList(string key)
    {
        lock (_gate)
        {
            if (_root[key] is not JsonArray array)
            {
                return null;
            }

            var list = new List<string>(array.Count);
            foreach (JsonNode? item in array)
            {
                if (item is not null)
                {
                    list.Add(item.GetValue<string>());
                }
            }

            return list;
        }
    }

    private T? TryGet<T>(string key)
        where T : struct
    {
        try
        {
            lock (_gate)
            {
                return _root[key] is { } node ? node.GetValue<T>() : null;
            }
        }
        catch
        {
            return null;
        }
    }

    public void Set(string key, object? value)
    {
        lock (_gate)
        {
            _root[key] = value switch
            {
                null => null,
                string s => JsonValue.Create(s),
                double d => JsonValue.Create(d),
                int i => JsonValue.Create(i),
                bool b => JsonValue.Create(b),
                IEnumerable<string> list => new JsonArray([.. list.Select(s => (JsonNode)JsonValue.Create(s)!)]),
                _ => JsonValue.Create(value.ToString()),
            };

            // 以前这里是直接 Save()，一次改动一次落盘。问题出在设置窗的滑块上：
            // 它们是 IsSnapToTickEnabled 的，拖一次会每跨一个刻度就触发一次
            // ValueChanged——不透明度那条从 0.3 拖到 1.0 就是 35 次。
            // 每次都是「写临时文件 + File.Move 替换」，而 %APPDATA% 底下的写入
            // 要过一遍实时杀毒扫描，几十毫秒起步。叠起来就是界面肉眼可见的卡顿，
            // 而且这一切都发生在 UI 线程上。
            //
            // 改成合并：内存里立刻生效，停手 300ms 之后才写一次盘。
            _dirty = true;
            _saveTimer.Change(SaveDelayMs, Timeout.Infinite);
        }
    }

    /// <summary>
    /// 立刻把挂起的改动写下去。退出前必须调用，否则最后 300ms 内改的设置会丢。
    /// </summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            Save();
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // 先写临时文件再替换：中途崩溃不会留下半个 JSON，
            // 否则下次启动设置会整个丢失。
            string temp = _path + ".tmp";
            File.WriteAllText(temp, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // 落盘失败不影响本次运行，不值得打断用户
        }
    }
}
