# 面试字幕 · Windows 版

[![Windows](https://github.com/yangli-0409/Awesome-Realtime-Translator/actions/workflows/windows.yml/badge.svg)](https://github.com/yangli-0409/Awesome-Realtime-Translator/actions/workflows/windows.yml)

macOS 版的 Windows 移植。同一个产品，同一条边界：**只帮你听懂，不替你回答。**

- 上级目录：[项目主 README](../README.zh-CN.md)
- 工程笔记：[macOS 版的实现记录](../docs/ENGINEERING.zh-CN.md)

---

## 一句话说清和 Mac 版的差别

Mac 版 0.8MB，因为识别和翻译都是 macOS 26 自带的系统能力。
Windows 没有这两样东西，所以得自己背着模型走，**安装包约 540MB**。

功能、交互、设计取舍全部对齐；差的是下面这张表。

| | macOS 版 | Windows 版 |
|---|---|---|
| 系统音频采集 | ScreenCaptureKit | WASAPI loopback |
| 需要的权限 | 屏幕与系统音频录制（每次重装都要重授权） | **不需要任何权限** |
| 语音识别 | 系统的 SpeechAnalyzer | 自带 whisper.cpp（142MB） |
| 翻译 | 系统的 Translation 框架 | 自带 OPUS-MT ONNX（240MB） |
| 对屏幕共享隐藏 | `NSWindow.sharingType = .none` | `SetWindowDisplayAffinity` |
| 不抢键盘焦点 | `canBecomeKey = false` | `WS_EX_NOACTIVATE` |
| 不进任务栏 / Dock | `LSUIElement` | `WS_EX_TOOLWINDOW` |
| 安装包 | 0.8 MB | 约 540 MB |
| 术语翻译质量 | 好（Apple 的模型） | 一般，靠内置术语表补救（见下） |

Windows 版**反而有一个地方更好**：不需要任何系统权限。没有 TCC 那一套，
也就没有 Mac 版那个「每次重新编译都要重新授权一次」的老大难。

## 装和用

需要 **Windows 10 2004（19041）及以上**。

1. 下载解压。**整个文件夹要保持原样**——`models`、`runtimes` 和那几个 dll
   都得和 exe 在一起，不能只把 exe 拖出来。要快捷方式就右键 exe → 发送到 → 桌面。
2. 双击 `AwesomeRealtimeTranslator.exe`。
3. 字幕窗出现在屏幕顶部，点右上角 ▶ 开始听。

没有安装程序，没有权限弹窗，不用重启。

没有买代码签名证书，所以 Windows 会弹「未知发布者」，极少数杀毒软件也可能误报。
这是这个分发方式的固有属性，不是 bug，只能靠签名彻底解决（见下）。

| | 位置 | 快捷键 |
|---|---|---|
| 开始 / 暂停 | 字幕窗右上角 ▶ | `Ctrl+Alt+T` |
| 显示 / 隐藏字幕窗 | 托盘菜单 | `Ctrl+Alt+H` |
| 设置 | 字幕窗右上角 ⚙ | — |
| 退出 | 字幕窗右上角 ⏻ | — |
| 复制 / 导出记录 | 托盘右键菜单 | — |

退出按钮必须在字幕窗上，和 Mac 版同样的理由：这个窗口不进任务栏、不接受键盘焦点，
`Alt+F4` 到不了它。没有这个按钮，用户只能去任务管理器结束进程。

## 从源码构建

```bash
dotnet tool  # 需要 .NET 10 SDK
```

```bash
cd windows && ./tools/fetch-models.sh && dotnet test tests/Core.Tests/Core.Tests.csproj
```

```bash
pwsh tools/package.ps1
```

**整个解决方案在 macOS 和 Linux 上也能构建**（`EnableWindowsTargeting`），
这个移植就是在一台 Mac 上写完的。但**测试分两半**，别在非 Windows 上
直接跑 `dotnet test`（那会连 `App.Tests` 一起拉进来，必然失败）：

```
windows/
├── src/Core/          平台无关：断句、显现节奏、管道装配、翻译
├── src/App/           Windows 专有：WASAPI、Whisper、WPF 界面、Win32 互操作
├── tests/Core.Tests/  87 个单测，任何平台都能跑
├── tests/App.Tests/   窗口构造测试，只有真 Windows 能跑（CI 上跑）
└── tools/             取模型、画图标、打包
```

分层的理由是**可验证性**：凡是能放进 Core 的逻辑都放了进去，于是它们在开发机上
就能真跑测试。留在 App 里的只有非 Windows 不可的部分。

### CI 替开发机跑 Windows

[`.github/workflows/windows.yml`](../.github/workflows/windows.yml) 在
`windows-latest` 上还原、构建、跑两个测试工程，另一个 job 构建 `win-arm64`。

**这是 0.1.5 之后才有的，而它存在的理由就是上面那个 bug。**
「能编译」被当成「能跑」整整四个版本，代价是一个真实用户在面试前踩雷。
`App.Tests` 做的事少到有点可笑——把每个 XAML 窗口 `new` 一次，断言标签和
滑块真的被填上了。不显示、不点击、不截图。但那个 bug 就在构造函数里，
所以这一下就够了（构造窗口不创建 HWND，CI runner 上照样跑）。

守的那条线是：**每个 XAML 窗口都必须能被 new 出来。**新加窗口就往
`WindowConstructionTests` 里加一个用例。

**这个测试被验证过是真的有牙**，不是写上去好看的。做法是把 0.1.5 的修复
回退到一个临时分支上、`workflow_dispatch` 跑一次：

| | 结果 |
|---|---|
| 四个 `SettingsWindow` 用例 | **全红**，堆栈就是线上那条：`NullReferenceException → UpdateLabels → OnFontSizeChanged → InitializeComponent` |
| `CaptionWindow` 用例 | 照常通过 —— 说明它不是无差别地红 |
| 耗时 | 约 3 秒 |

一个花了四个版本、一个真实用户、两轮错误诊断才抓到的 bug，在 CI 上三秒复现。
一个测试值不值，看的就是这个。

## 四件和 Mac 版实现思路不同的事

### 1. Whisper 不是流式的，流式语义是自己造的

macOS 的 SpeechAnalyzer 本身就吐 volatile / finalized 事件。Whisper 一次吃一整段音频，
所以 `WhisperTranscriber` 用「滑动窗口 + 重复解码」造出同样的语义：

- 每 700ms 把**整个当前窗口**重新解码一次 → 作为 volatile 发出去；
- 检测到停顿超过 600ms → 再解码一次 → 作为 finalized 发出去，清空窗口；
- 窗口超过 18 秒强制定稿，否则解码耗时会随窗口线性增长。

代价是同一段音频被反复解码好几遍，用 CPU 换「边说边出字」。

静音判定按**挂钟时间**算，不靠数静音采样点——WASAPI loopback 在系统完全没声音时
根本不给缓冲区，数采样点会永远数不满。同样的原因，采集层会播一路静音把音频会话钉住，
否则对方开口前的头几百毫秒会被吞掉。

### 2. 翻译要自己跑推理，技术词汇需要额外保护

OPUS-MT 是通用语料训练的，在软件面试这个场景里会把关键词翻错。实测：

```
architecture            → 建筑          （应为「架构」）
container orchestration → 集装箱的管弦   （应为「容器编排」）
```

试过两种修法：

1. 把中文术语直接塞进英文原句 → 模型产出乱码（`49/ 18 / 12 / 12 /`），废案。
2. 换成 `ZQX7` 这样的哨兵，翻完再换回中文 → 模型原样复制，位置也对。

所以走第二种，见 `TermProtector`。内置术语表只收**实测翻错**的词，不是凭印象堆的。
修好之后：

```
We use Kubernetes for container orchestration at scale.
→ 我们使用 Kubernetes 来计算容器编排的缩放。

We had a race condition that caused a deadlock in the message queue.
→ 我们有一个竞态条件, 导致消息队列的死锁。
```

语法偶尔别扭，但意思准确——这正是产品要的（只帮你听懂）。

### 3. 应用自己永远不联网

Mac 版「一次网络请求都没有」是靠系统自带模型做到的。Windows 要自带模型文件，
但**自带不等于运行时去下载**：一旦应用自己发请求，那条承诺就破了，而且用户没法
验证它只下了模型。

所以模型随安装包一起发，应用只在本地找；找不到就明确报错，绝不偷偷联网。
唯一的下载动作是用户自己在浏览器里下安装包。

### 4. 原生库不打进 exe（0.1.2 改掉的）

Mac 版是一个 `.app` 包，看起来是一个图标，其实是目录。Windows 这边 0.1.0/0.1.1
想模仿这种「一个文件」的观感，开了 `IncludeNativeLibrariesForSelfExtract=true`,
把 onnxruntime、WPF 的原生库、以及 `runtimes/win-*/` 下的 whisper 库全部塞进 exe。

这个选择是错的，两头都不成立：

**好处本来就不存在。** 语音模型 148MB、翻译模型 240MB，塞不进 exe，`models/`
必须和 exe 同目录。用户从头到尾拿到的都是一个文件夹，「单文件」只是自我安慰。

**代价是真的。** 开了这个开关的 exe 每次启动都要把原生库解压到
`%TEMP%\.net\` 再加载。「进程把可执行代码写进临时目录然后加载它」是杀毒软件
启发式规则里的典型 dropper 行为。更要紧的是 Whisper.net 的加载器是**按路径**
找库的（`runtimes/<rid>/whisper.dll`，见它自己的 `.targets`），而 0.1.1 的 exe
旁边根本没有这个目录。

对照构建把差别摊开了：

| | 0.1.1（自解压） | 0.1.2（现在） |
|---|---|---|
| exe | 171 MB | 147 MB |
| exe 旁边的原生库 | 无 | 6 个 dll + `runtimes/win-*/` 共 12 个 |
| 启动时往磁盘写东西 | 有 | **没有** |

0.1.2 只留托管程序集在 exe 里（从内存加载，不落盘），原生库放回 Whisper.net
和 .NET SDK 本来期望的位置。代价是用户不能只把 exe 拖走——写进了使用说明。

## 已经验证的 / 还没验证的

**这个移植是在一台 Mac 上开发的，没有 Windows 机器。** 下面这条分界线因此特别重要。

**2026-11-24 更新：有用户在真 Windows 上跑起来了。** 下面的分界线按这次反馈重画过。

**已验证（CI，windows-latest，0.1.5 起）**

- **两个 XAML 窗口都能在真 Windows 上构造出来**，而且标签和滑块真的被填上了。
  就是这一条在 0.1.0–0.1.4 之间漏掉了（见下）。
- `win-x64` 和 `win-arm64` 都零警告构建通过。
- Core.Tests 在 CI 上是 82 过 + **5 跳过**：那 5 个是翻译测试，需要真实的
  OPUS-MT 模型，CI 没取模型（389MB）所以自动跳过。它们在开发机上是真跑过的。

**已验证（开发机上）**

- 整个解决方案零警告编译，产物是真正的 Windows PE32+ 可执行文件。
- 87 个单元测试全过（含上面在 CI 上被跳过的那 5 个），覆盖断句（27 条从 Swift
  逐条移植，行为完全一致）、显现节奏、整条管道的装配与降级路径、术语保护。
- **翻译是真跑过的**，不是只编译通过：ONNX Runtime 跨平台，所以用真实模型在
  开发机上完成了端到端翻译，上面那些译文就是实际输出。单句 36~128ms。
- 管道的全部错误路径：识别器加载失败、翻译器降级、音频中断、停机冲刷。

**已验证（真实 Windows 用户，0.1.3 / 0.1.4 的日志）**

- **WASAPI loopback 真的抓到了声音。** 原来的第一大未知项，通过。
- Whisper.net 在 0.1.2 之后的目录布局下能找到原生库，模型加载正常。
- 整条链路出字幕、出译文。
- 反过来也验证出两件坏事，都有堆栈：设置窗从 0.1.0 起就没打开过，
  以及程序放在非系统盘时模型加载会崩。两条都在 0.1.5 里处理了（见下）。

**还没验证**

1. **`SetWindowDisplayAffinity` 到底有没有让字幕窗在共享里消失。**
   现在是唯一的头号未知项，也是这个产品最关键的承诺——代码里做了回读确认，
   失败会明确提示用户，但「回读说成功」和「Zoom 里真的看不到」之间还差一次实测。
2. Whisper 在 CPU 上的实际解码速度，以及端到端延迟到底是多少。
   700ms 的步长是按「base 模型解码 5 秒窗口约 200~400ms」估的，没实测过。
3. 全局快捷键是否被别的程序占用。
4. 长会话的稳定性、内存和 CPU 占用。
5. 在装了第三方杀毒软件的机器上会不会被误报拦截。
6. 设置窗的**交互**。构造已经由 CI 在真 Windows 上验证了（0.1.5 起），
   但「点 ⚙ 之后它是不是真的出现在你眼前」还没有：那取决于 `Topmost`
   和前台切换限制那段推理，而**那段推理从来没被实测验证过**。
7. 放在非系统盘上模型加载崩掉那件事，病因还没查清（见下）。

把第 1 条跑通，这个版本才算真的能用。其余都是调参。

### 设置窗从来没有打开过（0.1.5 修的）

第一次真实 Windows 反馈同时带来了一个 bug：**点「设置」卡死或闪退。**
0.1.4 猜了两个病因，**都猜错了**。真正的原因是 0.1.5 才定位到的，
而定位它靠的是 0.1.4 里唯一那个不是猜测的改动。这一节按时间顺序记，
因为过程本身比结论更值得留着。

**真正的病因：XAML 解析期间的 NullReferenceException**

```
NullReferenceException at SettingsWindow.UpdateLabels()
  ← SettingsWindow.OnFontSizeChanged()
  ← Slider.OnValueChanged()
  ← RangeBase.OnMinimumChanged()          ← 注意这里
  ← XamlObjectWriter.WriteEndMember()
  ← SettingsWindow.InitializeComponent()  ← 窗口还在构造中
```

XAML 里写的是 `<Slider x:Name="FontSizeSlider" Minimum="11" …>`，而 `Slider.Value`
的默认值是 0。解析器设 `Minimum=11` 的那一刻，WPF 把 Value 从 0 强制抬到 11，
**当场**触发 `ValueChanged` → `OnFontSizeChanged` → `UpdateLabels()`。
可此刻 `InitializeComponent()` 还在往下走，`FontSizeValue` 这个 TextBlock
在 XAML 里写在 Slider **后面**，字段还是 null。

四个滑块的 `Minimum` 都大于 0，所以四个都会这样触发。这不是偶发、不挑机器、
不挑时序：**从 0.1.0 到 0.1.4，设置窗在任何一台 Windows 机器上都没有真正打开过。**

原来的 `_loading` 守卫只挡住了「写配置」这一半：

```csharp
if (!_loading) { _settings.FontSize = e.NewValue; }
UpdateLabels();   // ← 没挡住，就漏在这里
```

修法是把守卫下沉到 `UpdateLabels()` 自己身上——一处生效，四个处理器全覆盖，
以后新加的处理器也不会再踩同一个坑。

**0.1.4 猜错的两条**

一是 `WS_EX_NOACTIVATE` 导致 `Activate()` 被前台切换限制拒绝、设置窗开在别人
后面。这个机制是真的、有文档的，但窗口压根没构造出来，走不到那一步。
代码保留了（代价为零，拿到焦点就自动撤掉 Topmost），但**到底需不需要它，
仍然没有被实测验证过**，注释里写明了。

二是滑块每跨一个刻度就同步落盘导致卡顿。`FileSettingsStore` 以前确实是
「写临时文件 + `File.Move` 替换」，一条不透明度滑块从 0.3 拖到 1.0 会触发
35 次，叠在 UI 线程上确实不合理。改成了停手 300ms 合并写一次、退出时
`Flush()` 兜底。这个改动本身是对的，但它也不是用户报的那个 bug——
用户连滑块都没见到过。

**0.1.4 真正起作用的那一条**

`AppDomain.UnhandledException` 和 `TaskScheduler.UnobservedTaskException` 都
接不到 WPF Dispatcher 上的异常，所以任何界面异常都会直接把进程带走，
用户看到的就是「闪退」，还什么说法都没有。0.1.4 挂上了
`DispatcherUnhandledException`，又在 `OpenSettings()` 外面单独包了一层
（托盘菜单的回调跑在 WinForms 的嵌套消息循环里，不一定经过 Dispatcher）：
记日志、弹个能看的提示、标成已处理。

**就是这一条换来了上面那段堆栈。** 教训很直白：在一个自己跑不起来的平台上，
与其对着症状猜病因，不如先保证下一次失败会带着证据回来。

### 放在 E 盘时模型加载直接崩（0.1.5 加了提示，病因没查清）

同一份日志里还有一条，和设置窗无关：

```
TranscriberException: 语音识别模型加载失败：External component has thrown an exception.
SEHException: External component has thrown an exception.
   at WhisperTranscriber.PrepareAsync(…)
```

`SEHException` 是原生层踩空了。日志里的证据很干净：

| 程序目录 | 启动次数 | 模型加载 |
|---|---|---|
| `E:\实时翻译\…` | 2 | **2 次全失败** |
| `C:\Users\…\Downloads\…` | 3 | 3 次全正常 |

文件大小检查是过了的（日志记的是 141 MB，正常值），所以不是下载到一半那种
明显的半截文件。做了两件事：

1. **加了文件头校验。** ggml 文件前 4 字节是 `GGML_FILE_MAGIC`（`0x67676d6c`，
   落到磁盘上是 ASCII `lmgg`，对着真实的 `ggml-base.bin` 用 xxd 核过）。
   大小对得上、内容是坏的这种情况确实存在，先验这一下就能给出一句人话，
   而不是把一个 `SEHException` 丢给用户。这也很可能就是 E 盘那次的真因。
2. **单独 catch 了 `SEHException`**，把上面那条经验直接写进错误提示：
   把整个文件夹复制到系统盘下再跑。

**病因没查清**，也没有 Windows 机器可以复现。所以提示的措辞是「已知有效的办法」，
不是「原因是」——这个区别在这个项目里已经吃过两次教训了。

### 托盘图标一直是系统默认图标

`LoadTrayIcon()` 去 `AppContext.BaseDirectory/Resources/app.ico` 找图标，
但 `<ApplicationIcon>` 只把图标嵌进 exe 的 PE 资源，不会往输出目录拷这个文件。
所以从 0.1.0 起托盘显示的一直是 Windows 通用图标。不影响功能，没在 0.1.2 里动它。
真要修就从 `Environment.ProcessPath` 里把嵌入的图标抽出来，不用多发文件。

## 许可证

[MIT](../LICENSE)，和主项目一致。
