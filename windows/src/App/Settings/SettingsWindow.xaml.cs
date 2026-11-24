using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AwesomeRealtimeTranslator.Core;

namespace AwesomeRealtimeTranslator.Settings;

/// <summary>
/// 设置窗，对应 macOS 版的 SettingsWindow + SettingsView。
///
/// 和 macOS 版一样是手工管理的单例窗口。那边的理由是 SwiftUI 的 Settings 场景
/// 要走 responder chain，而字幕窗不接受键盘焦点导致责任链是空的；
/// 这边没有那个问题，但保持同样的结构更好对照，也省得出现两个设置窗。
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;

    private readonly AppSettings _settings;
    private bool _loading = true;

    // internal 而不是 private：窗口构造测试要能 new 它。
    // 对外仍然只有 Show()，单例语义没变。
    internal SettingsWindow(AppSettings settings, string modelDirectory)
    {
        _settings = settings;
        InitializeComponent();

        foreach (DisplayMode mode in Enum.GetValues<DisplayMode>())
        {
            DisplayModeBox.Items.Add(new ComboBoxItem { Content = mode.Label(), Tag = mode });
        }

        DisplayModeBox.SelectedIndex = Array.IndexOf(Enum.GetValues<DisplayMode>(), settings.DisplayMode);

        foreach ((string id, string label) in SupportedLanguages.Sources)
        {
            SourceLanguageBox.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        }

        SelectLanguage(settings.SourceLanguage);

        FontSizeSlider.Value = settings.FontSize;
        OpacitySlider.Value = settings.Opacity;
        LinesSlider.Value = settings.VisibleLines;
        PaceSlider.Value = settings.RevealPace;
        ClickThroughBox.IsChecked = settings.ClickThrough;
        HideFromSharingBox.IsChecked = settings.HideFromScreenSharing;
        SaveTranscriptsBox.IsChecked = settings.SaveTranscripts;
        ModelPathText.Text = $"模型目录：{modelDirectory}";

        // 必须在 UpdateLabels() 之前放开：UpdateLabels 自己带 _loading 守卫。
        // 到这里控件值都已经赋完，放开它不会再触发任何写配置的回调。
        _loading = false;
        UpdateLabels();

        // 真的拿到焦点之后就撤掉 Topmost。它只是用来保证窗口能被看见，
        // 不是要一直压在别人头上。见 Show() 里的解释。
        Activated += (_, _) => Topmost = false;
    }

    /// <summary>
    /// 打开设置窗。
    ///
    /// <b>为什么要折腾 Topmost。</b>字幕窗带 WS_EX_NOACTIVATE——点它不会让本进程
    /// 变成前台进程，这是故意的（面试时打字不能被打断）。但副作用是：用户点 ⚙ 的
    /// 那一刻，前台进程仍然是浏览器或会议软件，于是这里的 Activate() 会被 Windows
    /// 的前台切换限制直接拒绝，设置窗<b>开在了前台窗口的后面</b>。
    ///
    /// 用户看到的就是「点设置没反应」。再点一次还是没反应，因为单例已经建好了，
    /// Show() 变成空操作，Activate() 继续被拒。
    ///
    /// <b>声明：这不是 0.1.4 之前「点设置就卡死」的原因</b>，那个是
    /// <see cref="UpdateLabels"/> 里的 NullReferenceException——窗口根本没构造出来，
    /// 压根走不到这里。0.1.4 当时就是照这段推理改的，改错了方向。
    /// 这段机制本身是真的（WS_EX_NOACTIVATE 的前台限制有文档可查），但因为
    /// 设置窗在 0.1.5 之前从来没打开过，<b>它到底需不需要仍然没有被实测验证过</b>。
    /// 保留是因为代价为零：拿到焦点就自动撤掉。
    ///
    /// Topmost 不受前台切换限制，窗口能浮上来；用户点一下它就正常拿到焦点，
    /// 然后 Activated 把 Topmost 撤掉。如果始终没拿到焦点，它就一直浮着——
    /// 对这个场景这正是想要的兜底。
    /// </summary>
    public static void Show(AppSettings settings, string modelDirectory)
    {
        if (_instance is null)
        {
            _instance = new SettingsWindow(settings, modelDirectory);
            _instance.Closed += (_, _) => _instance = null;
        }

        if (_instance.WindowState == WindowState.Minimized)
        {
            _instance.WindowState = WindowState.Normal;
        }

        _instance.Topmost = true;
        _instance.Show();
        _instance.Activate();
    }

    private void SelectLanguage(string id)
    {
        for (int i = 0; i < SourceLanguageBox.Items.Count; i++)
        {
            if (SourceLanguageBox.Items[i] is ComboBoxItem { Tag: string tag } && tag == id)
            {
                SourceLanguageBox.SelectedIndex = i;
                return;
            }
        }

        SourceLanguageBox.SelectedIndex = 0;
    }

    /// <summary>
    /// <b>这个守卫不是防御性编程，少了它必崩。</b>（0.1.5 修的，之前每台机器
    /// 点设置都 100% 崩溃或报错，设置窗从来没有真正打开过。）
    ///
    /// XAML 解析到 <c>&lt;Slider Minimum="11"&gt;</c> 时，WPF 会把 Value 的默认值 0
    /// 强制抬到 11，<b>当场</b>触发 ValueChanged → OnFontSizeChanged → 这里。
    /// 而此刻 InitializeComponent() 还在往下走，FontSizeValue 这些 TextBlock
    /// 在 XAML 里写在 Slider 后面，字段还是 null —— NullReferenceException。
    ///
    /// 四个 slider 的 Minimum 都大于 0，所以四个都会这样触发，每次开窗必中。
    /// 原来的 _loading 守卫只挡住了「写配置」，没挡住「刷标签」，就漏在这里。
    ///
    /// 构造函数末尾会主动调一次，所以加载期间跳过不会少更新任何东西。
    /// </summary>
    private void UpdateLabels()
    {
        if (_loading)
        {
            return;
        }

        FontSizeValue.Text = ((int)FontSizeSlider.Value).ToString(CultureInfo.InvariantCulture);
        OpacityValue.Text = OpacitySlider.Value.ToString("P0", CultureInfo.InvariantCulture);
        LinesValue.Text = ((int)LinesSlider.Value).ToString(CultureInfo.InvariantCulture);
        PaceValue.Text = PaceSlider.Value.ToString("0.00", CultureInfo.InvariantCulture) + "s";
        PaceHint.Text = DescribePace(PaceSlider.Value);
    }

    /// <summary>
    /// 把一个抽象的秒数翻译成用户能判断的观感。数字本身对用户没有意义，
    /// 「跟手但会跳」和「顺滑但滞后」才是他在选的东西。
    /// </summary>
    private static string DescribePace(double pace) => pace switch
    {
        < 0.4 => "最跟手，但能看到字一跳一跳地出现。",
        < 0.6 => "默认。基本连续，每批末尾有轻微停顿。",
        < 0.9 => "更顺滑，字比对方说话晚半秒以上。",
        _ => "最连续，也最滞后。",
    };

    private void OnDisplayModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DisplayModeBox.SelectedItem is not ComboBoxItem { Tag: DisplayMode mode })
        {
            return;
        }

        _settings.DisplayMode = mode;
    }

    private void OnSourceLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SourceLanguageBox.SelectedItem is not ComboBoxItem { Tag: string id })
        {
            return;
        }

        _settings.SourceLanguage = id;
    }

    private void OnFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading)
        {
            _settings.FontSize = e.NewValue;
        }

        UpdateLabels();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading)
        {
            _settings.Opacity = e.NewValue;
        }

        UpdateLabels();
    }

    private void OnLinesChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading)
        {
            _settings.VisibleLines = (int)e.NewValue;
        }

        UpdateLabels();
    }

    private void OnPaceChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loading)
        {
            _settings.RevealPace = e.NewValue;
        }

        UpdateLabels();
    }

    private void OnClickThroughChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _settings.ClickThrough = ClickThroughBox.IsChecked == true;
        }
    }

    private void OnHideFromSharingChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _settings.HideFromScreenSharing = HideFromSharingBox.IsChecked == true;
        }
    }

    private void OnSaveTranscriptsChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _settings.SaveTranscripts = SaveTranscriptsBox.IsChecked == true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
