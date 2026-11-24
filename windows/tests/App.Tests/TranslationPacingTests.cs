using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using AwesomeRealtimeTranslator.Caption;
using AwesomeRealtimeTranslator.Core;
using Xunit;

namespace AwesomeRealtimeTranslator.Ui.Tests;

/// <summary>
/// 0.2.1 以前，设置里的「显现节奏」滑块只绑在未定稿的<b>原文</b>尾巴上，
/// 译文是个普通 TextBlock 直接 <c>Text="{Binding Translation}"</c>。
/// 用户反馈「调了那个参数也没用」——他是对的，那根线物理上就没接。
///
/// 这一组用例在真 Windows 上把窗口显示出来、走一遍可视化树，
/// 确认译文控件确实是 <see cref="StreamingTextBlock"/> 并且 Pace 真的绑上了。
/// 纯粹是因为上一次就是「以为接上了」才出的事。
/// </summary>
public class TranslationPacingTests
{
    private static AppSettings Defaults() => new(new InMemorySettingsStore());

    private static CaptionWindow Build(CaptionStore store, AppSettings settings) =>
        new(store,
            settings,
            onToggle: () => Task.CompletedTask,
            onOpenSettings: () => { },
            onQuit: () => { });

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
            {
                yield return hit;
            }

            foreach (T nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    [Fact]
    public void TranslationIsRenderedByTheStreamingControl()
    {
        Sta.Run(() =>
        {
            var store = new CaptionStore();
            AppSettings settings = Defaults();
            settings.RevealPace = 0.8;

            CaptionWindow window = Build(store, settings);
            store.StartSession();

            var sentence = new Sentence("But we really looked at how leading brands communicate.", 0, 1);
            store.Append(sentence);
            store.SetTranslation("但我们真的研究了领先品牌的沟通方式", sentence.Id);

            Sta.ShowAndRender(window);

            StreamingTextBlock[] streaming = [.. Descendants<StreamingTextBlock>(window)];

            // 一个是原文的未定稿尾巴（x:Name="Pending"），一个是这一行的译文
            Assert.True(
                streaming.Length >= 2,
                $"译文应该也走 StreamingTextBlock，实际只找到 {streaming.Length} 个");

            StreamingTextBlock? translation = streaming.FirstOrDefault(
                b => b.FullText == "但我们真的研究了领先品牌的沟通方式");

            Assert.NotNull(translation);
            Assert.Equal(0.8, translation!.Pace);

            window.Close();
        });
    }

    [Fact]
    public void TranslationPaceFollowsTheSetting()
    {
        // 改设置之后译文控件的 Pace 要跟着走，否则滑块还是个摆设。
        Sta.Run(() =>
        {
            var store = new CaptionStore();
            AppSettings settings = Defaults();
            settings.RevealPace = 0.3;

            CaptionWindow window = Build(store, settings);
            store.StartSession();

            var sentence = new Sentence("Tell me about yourself.", 0, 1);
            store.Append(sentence);
            store.SetTranslation("说说你自己", sentence.Id);

            Sta.ShowAndRender(window);

            settings.RevealPace = 1.0;
            Sta.Settle(window);

            StreamingTextBlock? translation = Descendants<StreamingTextBlock>(window)
                .FirstOrDefault(b => b.FullText == "说说你自己");

            Assert.NotNull(translation);
            Assert.Equal(1.0, translation!.Pace);

            window.Close();
        });
    }

    [Fact]
    public void StreamingPrefixesDoNotResetTheRevealedText()
    {
        // 流式译文是 append-only 的：每次来的都是上一次的延长。
        // 节奏器必须接着往下放，不能归零重来——归零就是「每来一段闪一下」。
        Sta.Run(() =>
        {
            var block = new StreamingTextBlock { Pace = 0.5 };

            block.FullText = "但我们";
            block.Text = "但我";          // 假装已经放出了两个字

            block.FullText = "但我们真的研究了";

            Assert.Equal("但我", block.Text);
        });
    }

    [Fact]
    public void SwitchingToAnUnrelatedLineResetsTheReveal()
    {
        // ItemsControl 回收容器时，上一行的进度不能带到下一行，
        // 否则新的一行天生缺前几个字。
        Sta.Run(() =>
        {
            var block = new StreamingTextBlock { Pace = 0.5 };

            block.FullText = "但我们真的研究了";
            block.Text = "但我们真";

            block.FullText = "说说你自己";

            Assert.Equal(string.Empty, block.Text);
        });
    }
}
