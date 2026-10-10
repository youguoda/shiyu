using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 管理窗的「翻译记录」页（用户需求 2026-10-09）：翻译框与反向输入框的每一次翻译，新的在前；
/// 搜原文或译文、复制译文或原文、删除（可撤销）、清空全部（不可撤销，走确认）。与剪贴板历史
/// 是两本账——这一页的读写只碰翻译记录，条目一条不动；历史页的键与命令也不越界到这里。
/// </summary>
public partial class LibraryWindow
{
    private bool _logPageActive;
    private bool _logHooked;
    private int _logSelfWrites;
    private DispatcherTimer? _logSearchDebounce;

    /// <summary>刚删掉、5 秒内还能撤销的翻译记录（与历史页的撤销共用一条撤销条）。</summary>
    private List<TranslationRecord>? _undoLog;

    /// <summary>此刻展开着显示全文的那一行（只有单选时才有）。</summary>
    private TranslationLogItem? _expandedLog;

    /// <summary>空态里「去设置」用：打开设置窗并定位到某一项。</summary>
    internal Action<string>? OpenSettingsAt { get; init; }

    /// <summary>此刻在翻译记录页。</summary>
    internal bool LogPageActive => _logPageActive;

    /// <summary>切到翻译记录页（设置里的「查看翻译记录」、探针）。</summary>
    public void ShowTranslationLogPage() => SwitchPage(log: true);

    private void OnHistoryTabClick(object sender, RoutedEventArgs e) => SwitchPage(log: false);

    private void OnLogTabClick(object sender, RoutedEventArgs e) => SwitchPage(log: true);

    private void SwitchPage(bool log)
    {
        // 页签是 ToggleButton：点已选中的那个会把它点掉，这里总是摆回"只有当前页亮着"。
        HistoryTab.IsChecked = !log;
        LogTab.IsChecked = log;

        if (_logPageActive != log)
        {
            // 撤销跟着它的页走：切页即结清——Z 不该在另一页撤销这一页的删除。
            CommitUndoExpiry();
            HideToast();
            _logPageActive = log;

            var history = log ? Visibility.Collapsed : Visibility.Visible;
            var logPage = log ? Visibility.Visible : Visibility.Collapsed;
            SearchPill.Visibility = history;
            FilterButton.Visibility = history;
            CountLabel.Visibility = history;
            HistoryCommands.Visibility = history;
            HistoryContent.Visibility = history;
            LogSearchPill.Visibility = logPage;
            LogCountLabel.Visibility = logPage;
            LogCommands.Visibility = logPage;
            LogContent.Visibility = logPage;

            MoveToast(log ? LogContent : ListSide);
            Title = log ? "拾语 · 翻译记录" : "拾语 · 历史";
        }

        if (log)
        {
            HookLog();
            ReloadLog();
            LogList.Focus();
        }
        else
        {
            EntryList.Focus();
        }
    }

    /// <summary>撤销条只有一个：切页时把它搬到当前页的列表上（位置与边距不变）。</summary>
    private void MoveToast(Panel host)
    {
        if (ToastHost.Parent is Panel current && !ReferenceEquals(current, host))
        {
            current.Children.Remove(ToastHost);
            host.Children.Add(ToastHost);
        }
    }

    /// <summary>头一次进这一页才订阅：大多数时候用户只看历史，翻译记录的变动不必叫醒这扇窗。</summary>
    private void HookLog()
    {
        if (_logHooked)
        {
            return;
        }

        _logHooked = true;
        _store.TranslationLogChanged += OnLogChanged;
        Closed += (_, _) => _store.TranslationLogChanged -= OnLogChanged;
    }

    /// <summary>别处的变动（两个框新记了一条、设置里清空、到期清理在后台跑完）：在场就重读。</summary>
    private void OnLogChanged()
    {
        if (_logSelfWrites > 0)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            RefreshLogIfShown();
        }
        else
        {
            Dispatcher.BeginInvoke(RefreshLogIfShown);
        }
    }

    private void RefreshLogIfShown()
    {
        if (_logPageActive)
        {
            ReloadLog();
        }
    }

    private void OnLogSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_logSearchDebounce is null)
        {
            _logSearchDebounce = new DispatcherTimer { Interval = SearchDelay };
            _logSearchDebounce.Tick += (_, _) =>
            {
                _logSearchDebounce.Stop();
                ReloadLog();
            };
        }

        _logSearchDebounce.Stop();
        _logSearchDebounce.Start();
    }

    /// <summary>
    /// 重读一遍：尽量按 id 保住选中（用户正看着的那条不该跳走）；被删掉了就停在原来的位置。
    /// 计数与历史页同口径——"显示数 / 总数"。
    /// </summary>
    private void ReloadLog(int? keepIndex = null)
    {
        var query = LogSearchBox.Text.Trim();
        var searching = query.Length > 0;
        var previous = (LogList.SelectedItem as TranslationLogItem)?.Id;
        var now = DateTimeOffset.UtcNow;
        var clamp = 3 * ThemeManager.Content.ContentLine;

        var items = _store.TranslationLog(searching ? query : null)
            .Select(record => new TranslationLogItem(
                record,
                TranslationLogText.Meta(record, now),
                TranslationLogText.OneLine(record.Original),
                clamp))
            .ToList();
        LogList.ItemsSource = items;

        var index = previous is { } id ? items.FindIndex(item => item.Id == id) : -1;
        if (index < 0 && keepIndex is { } at)
        {
            index = Math.Min(at, items.Count - 1);
        }

        if (index < 0 && items.Count > 0)
        {
            index = 0;
        }

        LogList.SelectedIndex = index;
        if (index >= 0)
        {
            LogList.ScrollIntoView(items[index]);
        }

        var total = _store.CountTranslationLog();
        LogCountLabel.Text = $"{items.Count} / {total}";
        LogClearButton.IsEnabled = total > 0;
        ShowLogEmpty(TranslationLogText.Empty(items.Count, searching, _settings().TranslationLogEnabled));
        UpdateLogCommands();
    }

    private void ShowLogEmpty(EmptyStateCopy? copy)
    {
        if (copy is null)
        {
            LogEmpty.Visibility = Visibility.Collapsed;
            return;
        }

        LogEmpty.Title = copy.Headline;
        LogEmpty.Description = copy.Hint;
        LogEmpty.Content = copy.OfferClear
            ? EmptyAction("清除搜索", () => LogSearchBox.Clear())
            : !_settings().TranslationLogEnabled && OpenSettingsAt is { } open
                ? EmptyAction("去设置", () => open("translate.log"))
                : null;
        LogEmpty.Visibility = Visibility.Visible;
    }

    private static Button EmptyAction(string text, Action act)
    {
        var button = new Button { Content = text, Padding = new Thickness(16, 5, 16, 5), Cursor = Cursors.Hand };
        button.Click += (_, _) => act();
        return button;
    }

    private void OnLogSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLogExpansion();
        UpdateLogCommands();
    }

    /// <summary>
    /// 单独选中的那一行展开、显示全部（用户需求 2026-10-10：翻译记录里点一下就能看全文）：
    /// 原文整段、结果按 Markdown 排。多选时都收起——全选了要删，不该把整页撑开。键盘
    /// ↑↓ 走的也是选择，展开跟着走。
    /// </summary>
    private void UpdateLogExpansion()
    {
        var only = LogList.SelectedItems.Count == 1 ? LogList.SelectedItem as TranslationLogItem : null;
        if (ReferenceEquals(only, _expandedLog))
        {
            return;
        }

        if (_expandedLog is { } previous)
        {
            previous.Expanded = false;
        }

        _expandedLog = only;
        if (only is not null)
        {
            only.Expanded = true;
        }
    }

    private void UpdateLogCommands()
    {
        var any = LogList.SelectedItems.Count > 0;
        LogCopyButton.IsEnabled = any;
        LogCopyOriginalButton.IsEnabled = any;
        LogDeleteButton.IsEnabled = any;
    }

    /// <summary>选中的行，按列表里的先后（多选时 SelectedItems 是点选的先后）。</summary>
    private List<TranslationLogItem> SelectedLogItems()
        => LogList.SelectedItems.Cast<TranslationLogItem>()
            .OrderBy(item => LogList.Items.IndexOf(item))
            .ToList();

    private void OnLogCopyTranslation(object sender, RoutedEventArgs e) => CopyLog(translated: true);

    private void OnLogCopyOriginal(object sender, RoutedEventArgs e) => CopyLog(translated: false);

    private void OnLogDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(LogList, (DependencyObject)e.OriginalSource) is ListBoxItem)
        {
            CopyLog(translated: true);
        }
    }

    /// <summary>
    /// 复制：管理窗没有粘贴目标，复制就是这一页的"用"。多条之间空一行。经拾语自己的写入口，
    /// 不在剪贴板历史里再记一条（同历史页的复制）。
    /// </summary>
    private void CopyLog(bool translated)
    {
        var selected = SelectedLogItems();
        if (selected.Count == 0)
        {
            ShowLightFeedback("先选中一条记录");
            return;
        }

        var text = string.Join(
            Environment.NewLine + Environment.NewLine,
            selected.Select(item => translated ? item.Record.Translated : item.Record.Original));
        if (_clipboard?.SetText(text) != true)
        {
            ShowLightFeedback("复制失败：剪贴板被其他程序占用，稍后再试");
            return;
        }

        var what = translated ? "译文" : "原文";
        ShowLightFeedback(selected.Count == 1 ? $"已复制{what}" : $"已复制 {selected.Count} 条{what}");
    }

    private void OnLogDelete(object sender, RoutedEventArgs e) => DeleteLogSelection();

    /// <summary>删除所选：可撤销级（§6.5）不弹框，直接删 + 撤销条，与历史页同一个手感。</summary>
    private void DeleteLogSelection()
    {
        var selected = SelectedLogItems();
        if (selected.Count == 0)
        {
            return;
        }

        var index = LogList.SelectedIndex;
        var removed = new List<TranslationRecord>();
        _logSelfWrites++;
        try
        {
            foreach (var item in selected)
            {
                if (_store.DeleteTranslationRecord(item.Id))
                {
                    removed.Add(item.Record);
                }
            }
        }
        finally
        {
            _logSelfWrites--;
        }

        ReloadLog(keepIndex: index);
        if (removed.Count == 0)
        {
            return;
        }

        CommitUndoExpiry();
        _undoLog = removed;
        ShowUndoFeedback($"已删除 {removed.Count} 条");
    }

    /// <summary>撤销条与 Z 在翻译记录页落到这里：原样放回（同 id、同时间、同位置）。</summary>
    private void UndoLogDelete(List<TranslationRecord> records)
    {
        _undoLog = null;
        _logSelfWrites++;
        try
        {
            _store.RestoreTranslationRecords(records);
        }
        finally
        {
            _logSelfWrites--;
        }

        ReloadLog();
        HideToast();
        ShowLightFeedback($"已恢复 {records.Count} 条");
    }

    private void OnLogClearAll(object sender, RoutedEventArgs e)
    {
        CommitUndoExpiry();
        HideToast();

        int cleared;
        _logSelfWrites++;
        try
        {
            cleared = TranslationLogDialogs.AskClear(this, _store);
        }
        finally
        {
            _logSelfWrites--;
        }

        if (cleared > 0)
        {
            LogSearchBox.Clear();
            ReloadLog();
            ShowLightFeedback($"已清空 {cleared} 条");
        }
    }

    /// <summary>
    /// 翻译记录页自己的一小套键：Enter / C 复制译文、O 复制原文、D / Delete 删除、Z 撤销、
    /// ↑↓ 走列表（焦点在搜索框里也走）、Ctrl+F 搜索、Esc 先清搜索词、Ctrl+W 关窗。
    /// 历史页的键（置顶、收藏、标签、预览……）在这里没有对象，一概不接。
    /// </summary>
    private void OnLogPageKeyDown(KeyEventArgs e, Key key)
    {
        var modifiers = Keyboard.Modifiers;
        var none = modifiers == ModifierKeys.None;
        var typing = Keyboard.FocusedElement == LogSearchBox;

        switch (key)
        {
            case Key.F when modifiers == ModifierKeys.Control:
                e.Handled = true;
                LogSearchBox.Focus();
                LogSearchBox.SelectAll();
                break;

            case Key.W when modifiers == ModifierKeys.Control:
                e.Handled = true;
                CommitUndoExpiry();
                Close();
                break;

            case Key.A when modifiers == ModifierKeys.Control && !typing:
                e.Handled = true;
                LogList.SelectAll();
                break;

            case Key.Up or Key.Down when modifiers is ModifierKeys.None or ModifierKeys.Shift:
                e.Handled = true;
                MoveLogSelection(key == Key.Up ? -1 : +1, modifiers == ModifierKeys.Shift);
                break;

            case Key.Enter when typing:
                // 搜索中回车：落进列表，下一发 Enter 才是复制（同历史页）。
                e.Handled = true;
                LogList.Focus();
                break;

            case Key.Enter:
                e.Handled = true;
                CopyLog(translated: true);
                break;

            case Key.C when none && !typing:
                e.Handled = true;
                CopyLog(translated: true);
                break;

            case Key.O when none && !typing:
                e.Handled = true;
                CopyLog(translated: false);
                break;

            case Key.D or Key.Delete when none && !typing:
                e.Handled = true;
                DeleteLogSelection();
                break;

            case Key.Z when !typing && modifiers is ModifierKeys.None or ModifierKeys.Control:
                e.Handled = true;
                UndoDelete();
                break;

            case Key.Escape:
                e.Handled = true;
                if (LogSearchBox.Text.Length > 0)
                {
                    LogSearchBox.Clear();
                }

                break;
        }
    }

    private void MoveLogSelection(int step, bool extend)
    {
        var count = LogList.Items.Count;
        if (count == 0)
        {
            return;
        }

        var next = Math.Clamp(LogList.SelectedIndex + step, 0, count - 1);
        if (extend)
        {
            LogList.SelectedItems.Add(LogList.Items[next]);
        }
        else
        {
            LogList.SelectedIndex = next;
        }

        LogList.ScrollIntoView(LogList.Items[next]);
    }

#if DEBUG
    /// <summary>探针命令 translation-log 的管理窗一段：在哪一页、列了几条、计数怎么写；渲一张 PNG。</summary>
    internal string ProbeLogPage(string picture)
    {
        var first = LogList.Items.Count > 0 ? LogList.Items[0] as TranslationLogItem : null;
        var line = FormattableString.Invariant(
            $"library|logPage={_logPageActive}|logTab={LogTab.IsChecked}|items={LogList.Items.Count}|count={LogCountLabel.Text}|historyVisible={HistoryContent.IsVisible}|empty={LogEmpty.IsVisible}|title={Title}|firstMeta={first?.Meta}")
            + Environment.NewLine;

        // 点一下看全文（用户需求 2026-10-10）：单选的那一行展开、结果按 Markdown 排进去；
        // 全选时一行都不展开。
        LogList.SelectedIndex = 0;
        LogList.UpdateLayout();
        var container = LogList.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
        var full = container is null ? null : Tree.FindDescendant<MarkdownBox>(container);
        var single = FormattableString.Invariant(
            $"|single={first?.Expanded}|full={full?.IsVisible}|markdown={full is not null && first is not null && full.Shown == first.Translated}");
        App.RenderToPng(RootGrid, picture);

        LogList.SelectAll();
        LogList.UpdateLayout();
        var expanded = LogList.Items.OfType<TranslationLogItem>().Count(item => item.Expanded);
        LogList.SelectedIndex = 0;

        return line + FormattableString.Invariant($"expand{single}|multi={expanded}") + Environment.NewLine;
    }
#endif
}

/// <summary>
/// 翻译记录页的一行（只读视图）。收起时原文压成一行、结果至多三行（<see cref="ClampHeight"/>），
/// 结果去掉 Markdown 标记再显示；单独选中时展开（<see cref="Expanded"/>，用户需求 2026-10-10），
/// 原文整段、结果按 Markdown 排。
/// </summary>
internal sealed class TranslationLogItem(TranslationRecord record, string meta, string originalLine, double clampHeight)
    : System.ComponentModel.INotifyPropertyChanged
{
    private bool _expanded;
    private string? _preview;

    public TranslationRecord Record { get; } = record;

    public string Meta { get; } = meta;

    public string OriginalLine { get; } = originalLine;

    public double ClampHeight { get; } = clampHeight;

    public long Id => Record.Id;

    public string Original => Record.Original;

    public string Translated => Record.Translated;

    /// <summary>收起时的结果：标题、列表、加粗都去掉标记，几行之内读得顺。第一次要时才算。</summary>
    public string TranslatedPreview => _preview ??= MarkdownText.Plain(Record.Translated);

    public bool Expanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value)
            {
                return;
            }

            _expanded = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Expanded)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>两扇窗共用的「清空翻译记录」确认（设置页的「立即清空」与管理窗的「清空全部」）。</summary>
internal static class TranslationLogDialogs
{
    /// <summary>
    /// 不可撤销·全部级（§6.5）：按钮写明条数，默认焦点与 Enter 都落在取消。返回清掉了几条；
    /// 用户没点头、或本来就没有记录时为 0。条目一条不碰。
    /// </summary>
    public static int AskClear(Window owner, EntryStore store)
    {
        var total = store.CountTranslationLog();
        if (total == 0)
        {
            return 0;
        }

        var answer = ContentDialog.Show(
            owner,
            "清空翻译记录",
            $"将永久删除全部 {total} 条翻译记录，无法撤销。剪贴板历史不受影响。",
            new ContentDialogButton("取消", ContentDialogButtonStyle.Standard, IsCancelFocus: true),
            new ContentDialogButton($"清空 {total} 条", ContentDialogButtonStyle.Danger));

        return answer == 1 ? store.ClearTranslationLog() : 0;
    }
}
