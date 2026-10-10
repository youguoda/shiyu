using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 管理窗详情三态（票 24 / UI 报告 §6.4）：未选中 = 空态 + 键位提示；单选 =
/// 元信息头 → 正文（散文用比例字体，等宽只给代码、路径、颜色）→ 属性区
/// （标签 / 备注 / 归组，行尾键帽）；多选 = 批量面板（"已选 n 条" + 构成 +
/// 整理组 + AI 组，动作带条数）。Agent 结果也住这里——详情栏是"正在读"的
/// 地方。全屏预览（按住 Space）同属本文件。
/// </summary>
public partial class LibraryWindow
{
    /// <summary>详情体当前内容（避免无谓重建）。</summary>
    private object? _detailState;

    private void UpdateDetail()
    {
        if (_store is null)
        {
            return;
        }

        var selected = SelectedItems();

        // Agent 运行中：结果面板占住详情（用户此刻读的是它）。
        if (_agentRun is not null)
        {
            return;
        }

        var state = selected.Count switch
        {
            0 => "empty",
            1 => $"one:{selected[0].Id}",
            _ => $"many:{selected.Count}:{string.Join(',', selected.Select(item => item.Id).OrderBy(id => id).Take(64))}",
        };
        if (state == (string?)_detailState)
        {
            return;
        }

        _detailState = state;
        DetailBody.Children.Clear();

        if (selected.Count == 0)
        {
            BuildDetailEmpty();
        }
        else if (selected.Count == 1)
        {
            BuildDetailSingle(selected[0]);
        }
        else
        {
            BuildDetailBatch(selected);
        }
    }

    /// <summary>空态（§6.4 详情）：说得出下一步，而不像加载失败。</summary>
    private void BuildDetailEmpty()
    {
        var empty = new EmptyState
        {
            Glyph = "\uE8B7",
            Title = "选择左侧的一条记录",
            Description = "↑↓ 移动 · Shift+↑↓ 连选 · Enter 复制 · Ctrl+F 搜索",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 48, 0, 48),
        };
        DetailBody.Children.Add(empty);
    }

    // --- 单选 -------------------------------------------------------------------

    private void BuildDetailSingle(EntryItem item)
    {
        var entry = _store.Get(item.Id);

        DetailBody.Children.Add(BuildDetailMetaHeader(item, entry));

        if (item.Thumbnail is not null)
        {
            var image = new Image
            {
                MaxHeight = 320,
                HorizontalAlignment = HorizontalAlignment.Left,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 12, 0, 0),
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            DetailBody.Children.Add(image);
            if (entry is not null)
            {
                FillDetailImage(entry, image, DetailBody.ActualWidth > 0 ? DetailBody.ActualWidth : 560, 320);
            }
        }

        // 正文：散文用比例字体（Type.Content），等宽只给代码、路径、颜色——
        // 评审 3.8 P1 点名的"中文散文用等宽"在此纠正。
        var mono = entry is null
            ? false
            : entry.Kind == EntryKind.Files
               || entry.Subtype is EntrySubtype.Link or EntrySubtype.LocalPath or EntrySubtype.Color;

        var body = new TextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            MinHeight = 96,
            Margin = new Thickness(0, 12, 0, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Text = entry is null ? item.Preview
                : entry.Kind == EntryKind.Files ? string.Join(Environment.NewLine, entry.Files)
                : entry.Text,
        };
        body.SetResourceReference(TextBox.FontSizeProperty, mono ? "Type.ContentMono" : "Type.Content");
        if (mono)
        {
            body.SetResourceReference(TextBox.FontFamilyProperty, "Font.Mono");
        }

        DetailBody.Children.Add(body);

        BuildDetailAttributes(item, entry);
    }

    private FrameworkElement BuildDetailMetaHeader(EntryItem item, Entry? entry)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };

        var local = item.CreatedAt.ToLocalTime();
        var uses = entry?.UseCount ?? 0;
        var words = item.Kind == EntryKind.Image
            ? (entry?.ImageWidth > 0 ? $"{entry!.ImageWidth}×{entry.ImageHeight}" : "图片")
            : $"{item.Text.Length} 字";

        var meta = new TextBlock
        {
            Text = $"{local:yyyy-MM-dd HH:mm}  ·  {(string.IsNullOrEmpty(item.SourceApp) ? "未知来源" : item.SourceApp)}"
                + $"  ·  {words}  ·  使用 {uses} 次",
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = item.Text.Length > 0 ? item.Text[..Math.Min(200, item.Text.Length)] : null,
        };
        meta.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
        meta.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        header.Children.Add(meta);
        return header;
    }

    /// <summary>
    /// How many detail-picture requests have been made. Incremented per
    /// selection; a decode that comes back holding an older number describes
    /// an entry the user has already moved past.
    /// </summary>
    private int _detailImageRequests;

    /// <summary>
    /// The detail picture, decoded on the thread pool (O-36): a 4K original
    /// is megabytes of PNG, and the decode used to happen between the list's
    /// two paints. The counter keeps a fast walk down the list from landing
    /// one entry's picture on another's row.
    ///
    /// Shown and decoded the way the bar's preview is (用户实录 2026-10-09：
    /// 预览图片是低分辨率的): fitted into the box at one image pixel per screen
    /// pixel at most, decoded at the screen pixels it is shown with — a fixed
    /// 640 was blown up on any scaled screen and on any wide pane.
    /// </summary>
    private void FillDetailImage(Entry entry, Image target, double boxWidth, double boxHeight)
    {
        var request = ++_detailImageRequests;
        target.Source = null;

        // The box lands on whole screen pixels and the original decodes to
        // exactly them; a row without a stored size decodes by width alone.
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var decode = (Width: PreviewSizing.DecodeWidth(0, boxWidth, scale), Height: 0);
        if (entry.ImageWidth > 0 && entry.ImageHeight > 0)
        {
            var display = PreviewSizing.FitImage(entry.ImageWidth, entry.ImageHeight, boxWidth, boxHeight, scale);
            target.MaxWidth = display.Width;
            target.MaxHeight = display.Height;
            decode = PreviewSizing.DecodeSize(entry.ImageWidth, entry.ImageHeight, display, scale);
        }

        var original = entry.HasOriginal ? entry.OriginalPath : null;
        var thumbnail = entry.ThumbnailPng;
        var dispatcher = Dispatcher;

        Task.Run(() =>
        {
            var source = original is { Length: > 0 } path ? DecodeImageFile(path, decode.Width, decode.Height) : null;
            source ??= AppIconCache.Decode(thumbnail, 480);

            dispatcher.BeginInvoke(() =>
            {
                if (request != _detailImageRequests)
                {
                    return;
                }

                if (source is null)
                {
                    // expected: 原图损坏或已被清理——收起图片区，条目本身照常。
                    target.Visibility = Visibility.Collapsed;
                    return;
                }

                target.Source = source;
            });
        });
    }

    /// <summary>
    /// Decodes an image file off the calling thread; null when it cannot be read.
    /// A height of 0 keeps the picture's shape from the width.
    /// </summary>
    private static ImageSource? DecodeImageFile(string path, int pixelWidth, int pixelHeight)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = pixelWidth;
            if (pixelHeight > 0)
            {
                image.DecodePixelHeight = pixelHeight;
            }

            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception failure) when (
            failure is IOException or UnauthorizedAccessException
            or NotSupportedException or FileFormatException)
        {
            // expected: an unreadable or vanished original (deleted behind
            // the list, offline share) shows the entry without a preview.
            return null;
        }
    }

    /// <summary>属性区（§6.4 单选）：标签 / 备注 / 归组，行尾键帽（L0 教学）。</summary>
    private void BuildDetailAttributes(EntryItem item, Entry? entry)
    {
        var section = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        section.SetResourceReference(TextElement.FontSizeProperty, "Type.Body");

        // 标签行
        var tagsRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = false };
        AddPropertyLabel(tagsRow, "标签");

        var tagsHost = new WrapPanel();
        DockPanel.SetDock(tagsHost, Dock.Left);
        foreach (var tag in entry?.Tags ?? [])
        {
            tagsHost.Children.Add(BuildTagChip(tag));
        }

        var addTag = new Button
        {
            Content = "＋ 加标签",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "加标签 · T",
        };
        addTag.SetResourceReference(StyleProperty, "FlyoutButton");
        addTag.Click += (_, _) => OpenTagMenu();
        tagsHost.Children.Add(addTag);
        tagsRow.Children.Add(tagsHost);
        AddRowKeyCap(tagsRow, "T");
        section.Children.Add(tagsRow);

        // 备注行
        var noteRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        AddPropertyLabel(noteRow, "备注");
        var note = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry?.Note) ? "未加备注" : entry!.Note,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (string.IsNullOrWhiteSpace(entry?.Note))
        {
            note.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
        }

        DockPanel.SetDock(note, Dock.Left);
        noteRow.Children.Add(note);

        var editNote = new Button
        {
            Content = string.IsNullOrWhiteSpace(entry?.Note) ? "添加" : "编辑",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "备注 · N",
        };
        editNote.SetResourceReference(StyleProperty, "FlyoutButton");
        editNote.Click += (_, _) => EditNoteSelection();
        DockPanel.SetDock(editNote, Dock.Left);
        noteRow.Children.Add(editNote);
        AddRowKeyCap(noteRow, "N");
        section.Children.Add(noteRow);

        // 归组行
        var groupName = entry is null ? null : _store.GroupOf(entry)?.Name;
        var groupRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        AddPropertyLabel(groupRow, "归组");
        var group = new TextBlock
        {
            Text = groupName is { Length: > 0 } name ? name : "未分组",
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (groupName is not { Length: > 0 })
        {
            group.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextTertiary");
        }

        DockPanel.SetDock(group, Dock.Left);
        groupRow.Children.Add(group);

        var moveGroup = new Button
        {
            Content = groupName is { Length: > 0 } ? "改组" : "归组",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "归组 · G",
        };
        moveGroup.SetResourceReference(StyleProperty, "FlyoutButton");
        moveGroup.Click += (_, _) => OpenGroupMenu();
        DockPanel.SetDock(moveGroup, Dock.Left);
        groupRow.Children.Add(moveGroup);
        AddRowKeyCap(groupRow, "G");
        section.Children.Add(groupRow);

        DetailBody.Children.Add(section);
    }

    private static void AddPropertyLabel(DockPanel row, string label)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        text.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);
        var gap = new Border { Width = 8 };
        DockPanel.SetDock(gap, Dock.Left);
        row.Children.Add(gap);
    }

    /// <summary>属性行尾的键帽（§5.2 L0：tooltip 和行内都教）。</summary>
    private static void AddRowKeyCap(DockPanel row, string key)
    {
        var cap = new ContentControl { Content = key, VerticalAlignment = VerticalAlignment.Center };
        cap.SetResourceReference(StyleProperty, "KeyCap");
        cap.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(cap, Dock.Right);
        row.Children.Add(cap);
    }

    private Button BuildTagChip(string tag)
    {
        var chip = new Button
        {
            Padding = new Thickness(8, 2, 6, 2),
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = Cursors.Hand,
            ToolTip = "移除这个标签",
        };
        chip.SetResourceReference(StyleProperty, "FlyoutButton");
        chip.SetResourceReference(Control.BackgroundProperty, "Brush.SurfaceSubtle");
        // 复合内容派生不出名字；动作与 tooltip 同一句话（票 27 / U-29）。
        System.Windows.Automation.AutomationProperties.SetName(chip, $"移除标签 {tag}");
        chip.Click += (_, _) => RemoveTagFromSelection(tag);

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var text = new TextBlock { Text = "#" + tag, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
        var close = new TextBlock { Text = "\uE711", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        close.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icon");
        close.SetResourceReference(TextBlock.FontSizeProperty, "Size.IconXs");
        close.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        panel.Children.Add(text);
        panel.Children.Add(close);
        chip.Content = panel;
        return chip;
    }

    // --- 多选批量面板 -------------------------------------------------------------

    /// <summary>批量面板（§6.4 多选）："已选 n 条" + 构成 + 整理组 + AI 组，动作带条数。</summary>
    private void BuildDetailBatch(List<EntryItem> selected)
    {
        var texts = selected.Count(item => item.Kind == EntryKind.Text);
        var images = selected.Count(item => item.Kind == EntryKind.Image);
        var files = selected.Count(item => item.Kind == EntryKind.Files);

        var title = new TextBlock { Text = $"已选 {selected.Count} 条" };
        title.SetResourceReference(TextBlock.FontSizeProperty, "Type.Subtitle");
        title.SetResourceReference(TextElement.FontWeightProperty, "Weight.Emphasis");
        DetailBody.Children.Add(title);

        var mix = new TextBlock
        {
            Text = $"构成：文本 {texts} · 图片 {images} · 文件 {files}",
            Margin = new Thickness(0, 4, 0, 0),
        };
        mix.SetResourceReference(TextBlock.FontSizeProperty, "Type.Body");
        mix.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        DetailBody.Children.Add(mix);

        // 整理组（§6.4：多选时的整理动作）。
        DetailBody.Children.Add(BuildDetailSectionLabel("整理"));
        var organize = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        organize.Children.Add(BuildBatchButton($"置顶 {selected.Count} 条", () => OnTogglePin(this, new RoutedEventArgs())));
        organize.Children.Add(BuildBatchButton($"收藏 {selected.Count} 条", () => OnToggleFavorite(this, new RoutedEventArgs())));
        organize.Children.Add(BuildBatchButton("加标签…", OpenTagMenu));
        organize.Children.Add(BuildBatchButton("归组…", OpenGroupMenu));
        DetailBody.Children.Add(organize);

        // AI 组（§6.4：动作带条数）。
        DetailBody.Children.Add(BuildDetailSectionLabel("AI 处理"));
        var ai = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        foreach (var (label, kind) in new[]
                 {
                     ($"总结 {selected.Count} 条", AgentActionKind.Summarise),
                     ("合并成笔记", AgentActionKind.MergeIntoNote),
                     ("改写", AgentActionKind.Rewrite),
                     ("建议标签", AgentActionKind.SuggestTags),
                 })
        {
            ai.Children.Add(BuildBatchButton(label, () => RunAgent(kind)));
        }

        ai.Children.Add(BuildBatchButton($"批量翻译 {texts} 条", RunTranslateBatch));
        DetailBody.Children.Add(ai);
    }

    private FrameworkElement BuildDetailSectionLabel(string label)
    {
        var text = new TextBlock { Text = label, Margin = new Thickness(0, 16, 0, 0) };
        text.SetResourceReference(TextBlock.FontSizeProperty, "Type.Caption");
        text.SetResourceReference(TextElement.FontWeightProperty, "Weight.Emphasis");
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        return text;
    }

    private Button BuildBatchButton(string label, Action action)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
        };
        button.SetResourceReference(StyleProperty, "FlyoutButton");
        button.SetResourceReference(Control.BackgroundProperty, "Brush.SurfaceSubtle");
        button.Click += (_, _) => action();
        return button;
    }

    // --- 错误反馈（§6.4 反馈：错误用详情栏顶部的 InfoBar）---------------------------

    private void ShowError(string title, string message)
    {
        DetailNotice.Severity = InfoBarSeverity.Danger;
        DetailNotice.Title = title;
        DetailNotice.Message = message;
        DetailNotice.IsOpen = true;
    }

    // --- 全屏预览（§6.4：按住 Space；单栏窄窗下"看全文"的唯一途径）-------------------

    private bool _previewOpen;

    private void OpenPreview()
    {
        if (_previewOpen || Primary is not { } item)
        {
            return;
        }

        PreviewBody.Children.Clear();

        if (item.Thumbnail is not null && _store.Get(item.Id) is { } entry)
        {
            var image = new Image
            {
                MaxHeight = 520,
                HorizontalAlignment = HorizontalAlignment.Left,
                Stretch = Stretch.Uniform,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            PreviewBody.Children.Add(image);

            // 预览层还没摆过（Collapsed），宽度从内容区取：减去预览正文的左右边距 24 + 24。
            FillDetailImage(entry, image, Math.Max(200, HistoryContent.ActualWidth - 48), 520);
        }
        else if (item.Kind == EntryKind.Files && _store.Get(item.Id) is { } files)
        {
            var list = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                Text = string.Join(Environment.NewLine, files.Files),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };
            list.SetResourceReference(TextBox.FontSizeProperty, "Type.ContentMono");
            list.SetResourceReference(TextBox.FontFamilyProperty, "Font.Mono");
            PreviewBody.Children.Add(list);
        }
        else
        {
            var text = new TextBlock
            {
                Text = item.Text,
                TextWrapping = TextWrapping.Wrap,
            };
            text.SetResourceReference(TextBlock.FontSizeProperty, "Type.Content");
            text.SetResourceReference(TextBlock.LineHeightProperty, "Line.Content");
            text.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
            PreviewBody.Children.Add(text);
        }

        PreviewHint.Text = "松开 Space 关闭 · Esc";
        PreviewLayer.Visibility = Visibility.Visible;
        _previewOpen = true;
    }

    private void ClosePreview()
    {
        if (!_previewOpen)
        {
            return;
        }

        PreviewLayer.Visibility = Visibility.Collapsed;
        _previewOpen = false;
        PreviewBody.Children.Clear();
    }

    // --- Agent 结果区（多选批量面板与「⋯ › AI 处理」的输出都住这里）------------------

    private AgentRun? _agentRun;
    private TextBlock? _agentOutputView;
    private TextBlock? _agentTitleView;
    private ItemsControl? _suggestedTagsView;

    /// <summary>
    /// Agent 动作要的是通用对话模型，只有自备密钥供得起（ADR-0009/票 08）。
    /// 没配就收起入口并说明去哪——而不是点了才在结果框里报一次运行时失败。
    /// </summary>
    private void RefreshAgentActions()
    {
        SyncMoreMenuActions();
    }

    /// <summary>
    /// Runs an action over exactly what the user selected. Every path to a
    /// model request starts here: a selection the user made and a button the
    /// user pressed. There is no automatic, background or per-entry
    /// processing anywhere in Shiyu.
    /// </summary>
    private async void RunAgent(AgentActionKind kind)
    {
        // async void 逃出去的异常是进程级崩溃（O-05）：面板组装与运行途中
        // 的意外要么记进日志，要么变成详情栏 InfoBar 的一句人话，绝不带走
        // 常驻的记录工具。
        try
        {
            var selected = SelectedItems();
            if (selected.Count == 0)
            {
                ShowLightFeedback("请先选中要处理的条目");
                return;
            }

            var entries = selected
                .Select(item => new Entry(item.Id, item.Text, item.SourceApp, DateTimeOffset.UtcNow))
                .ToList();

            // 排除名单是唯一闸口，Agent 动作也过它（O-17）：规则是上周才加
            // 的，也要拦得住上个月记下的密码被今天的总结送出去。
            var (sendable, skipped) =
                CaptureGate.SplitSendable(entries, _settings().BuildExclusionPolicy());
            if (sendable.Count == 0)
            {
                ShowLightFeedback("所选条目全部来自排除名单里的应用，已跳过。");
                return;
            }
            if (skipped > 0)
            {
                ShowLightFeedback($"已跳过 {skipped} 条来自排除名单应用的条目。");
            }

            ShowAgentView($"{AgentActions.Label(kind)} · {sendable.Count} 条");

            var run = new AgentRun(_model());
            _agentRun = run;
            _detailState = null;
            run.Updated += () => Dispatcher.Invoke(() =>
            {
                if (!ReferenceEquals(_agentRun, run))
                {
                    return;
                }

                if (run.Output.Length > 0 && _agentOutputView is not null)
                {
                    _agentOutputView.Text = run.Output;
                }

                if (run.State == TranslationState.Failed && _agentOutputView is not null)
                {
                    // An unreachable agent leaves everything else working; the
                    // message says so rather than looking like a broken window.
                    ShowError("处理失败", run.Error ?? "模型服务没有给出结果。");
                    CloseAgentView();
                }
            });

            await run.RunAsync(kind, sendable);

            if (kind == AgentActionKind.SuggestTags && run.State == TranslationState.Finished)
            {
                if (_suggestedTagsView is not null)
                {
                    _suggestedTagsView.ItemsSource = AgentActions.ParseSuggestedTags(run.Output);
                }

                if (_agentOutputView is not null)
                {
                    _agentOutputView.Text = "点击下面的标签即可加到所选条目：";
                }
            }
        }
        catch (Exception failure)
        {
            // RunAsync 已把模型失败收敛成状态；这里是面板自身的意外。
            Log.Event(LogEvent.AgentActionFailed, failure);
            CloseAgentView();
            ShowError("处理失败", failure.Message);
        }
    }

    /// <summary>Agent 结果面板（§6.4 详情）：标题 + 流式输出 + 建议标签 + 复制。</summary>
    private void ShowAgentView(string title)
    {
        DetailBody.Children.Clear();

        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };

        var heading = new TextBlock { Text = title };
        heading.SetResourceReference(TextBlock.FontSizeProperty, "Type.Subtitle");
        heading.SetResourceReference(TextElement.FontWeightProperty, "Weight.Emphasis");
        DockPanel.SetDock(heading, Dock.Left);
        head.Children.Add(heading);

        var copy = new Button
        {
            Content = "复制结果",
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
        };
        copy.SetResourceReference(StyleProperty, "FlyoutButton");
        copy.Click += (_, _) => CopyAgentOutput(copy);
        DockPanel.SetDock(copy, Dock.Right);
        head.Children.Add(copy);

        var close = new Button
        {
            Content = "关闭",
            Padding = new Thickness(12, 4, 12, 4),
            Cursor = Cursors.Hand,
        };
        close.SetResourceReference(StyleProperty, "FlyoutButton");
        close.Click += (_, _) => CloseAgentView();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        DetailBody.Children.Add(head);

        _agentTitleView = heading;

        // AI 结果是被阅读的正文：Type.Content 18/31（ADR-0012 排版 2）。
        var output = new TextBlock
        {
            Text = "正在处理…",
            TextWrapping = TextWrapping.Wrap,
        };
        output.SetResourceReference(TextBlock.FontSizeProperty, "Type.Content");
        output.SetResourceReference(TextBlock.LineHeightProperty, "Line.Content");
        output.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
        _agentOutputView = output;
        DetailBody.Children.Add(output);

        var tags = new ItemsControl { Margin = new Thickness(0, 12, 0, 0) };
        var panel = new System.Windows.FrameworkElementFactory(typeof(WrapPanel));
        tags.ItemsPanel = new ItemsPanelTemplate(panel);
        var template = new DataTemplate();
        var chipFactory = new System.Windows.FrameworkElementFactory(typeof(Button));
        chipFactory.SetBinding(Button.ContentProperty, new System.Windows.Data.Binding());
        chipFactory.AddHandler(Button.ClickEvent, new RoutedEventHandler(OnAcceptSuggestedTag));
        template.VisualTree = chipFactory;
        tags.ItemTemplate = template;
        _suggestedTagsView = tags;
        DetailBody.Children.Add(tags);
    }

    private void CloseAgentView()
    {
        _agentRun = null;
        _agentOutputView = null;
        _agentTitleView = null;
        _suggestedTagsView = null;
        _detailState = null;
        UpdateDetail();
    }

    private void OnAcceptSuggestedTag(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string tag })
        {
            return;
        }

        var selected = SelectedItems();
        foreach (var item in selected)
        {
            SelfWrite(() => _store.AddTag(item.Id, tag));
        }

        RefreshTagChoices();
        Reload();
        ShowLightFeedback($"已把「{tag}」加到 {selected.Count} 条");
    }

    private void CopyAgentOutput(Button source)
    {
        if (_agentRun?.Output is { Length: > 0 } output)
        {
            source.Content = _clipboard?.SetText(output) == true ? "已复制" : "复制失败";
        }
    }

    // --- 批量翻译（「⋯ › AI 处理」与批量面板共用）--------------------------------------

    private CancellationTokenSource? _translateBatch;

    /// <summary>
    /// Translates the selection one entry at a time and files each result as
    /// a linked translation. Only what is selected is ever sent; cancelling
    /// keeps everything already filed — a half-done batch is still half done.
    /// </summary>
    private async void RunTranslateBatch()
    {
        if (_translateBatch is not null)
        {
            return;
        }

        var selected = SelectedItems()
            .Where(item => item.Kind == EntryKind.Text && item.TranslatedFrom is null)
            .Select(item => item.Id)
            .ToList();

        if (selected.Count == 0)
        {
            ShowLightFeedback("先选中要翻译的文本条目。");
            return;
        }

        var settings = _settings();
        _translateBatch = new CancellationTokenSource();
        SetActionsBusy(true);

        var progress = new Progress<(int Done, int Total)>(step =>
            ShowLightFeedback($"批量翻译 {step.Done}/{step.Total}……"));

        try
        {
            var batch = new TranslationBatch(
                _store, _pipeline!, settings.BuildExclusionPolicy(), _model(),
                settings.TargetLanguage, settings.SourceLanguage);
            var result = await batch.RunAsync(selected, progress, _translateBatch.Token);

            // 报告带原因（票 08）：失败多少、为什么，一句话说完。
            ShowLightFeedback(_translateBatch.IsCancellationRequested
                ? $"已取消：成功 {result.Translated} 条，已完成部分已保留。"
                : "批量翻译完成：成功 " + result.Translated + " 条"
                  + (result.Skipped > 0 ? $"，跳过 {result.Skipped} 条（排除规则或已是译文）" : string.Empty)
                  + (result.Failed > 0
                      ? $"，失败 {result.Failed} 条（{result.FailureReason ?? "原因未知"}）"
                      : string.Empty)
                  + "。");
            Reload();
        }
        catch (OperationCanceledException)
        {
            ShowLightFeedback("批量翻译已取消，已完成部分已保留。");
        }
        catch (Exception failure)
        {
            // A dead backend must cost nothing but this one sentence —
            // 以及一行日志（O-24）：批量失败的实际原因只在日志里看得全。
            Log.Event(LogEvent.BatchTranslationFailed, failure, ("selected", selected.Count));
            ShowError("翻译服务不可用", failure.Message);
        }
        finally
        {
            _translateBatch.Dispose();
            _translateBatch = null;
            SetActionsBusy(false);
        }
    }

    /// <summary>
    /// 批量翻译跑动时，会与它赛跑同一些行的写按钮先让路（票 32）；
    /// 翻译本身不可取消地占住的是「开始第二批」。
    /// </summary>
    private void SetActionsBusy(bool busy)
    {
        DeleteButton.IsEnabled = !busy;
        CopyButton.IsEnabled = !busy;
        PinButton.IsEnabled = !busy;
        FavoriteButton.IsEnabled = !busy;
    }
}
