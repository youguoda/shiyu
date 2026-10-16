using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// The backup buttons' whole journey: file pickers, the password ask, the
/// progress window, and the plain-words result.
///
/// The password is handled as bytes from the first ask to the wipe inside
/// BackupArchive — it is never a string, because a managed string cannot be
/// cleared and "wiped after use" would be a claim the code cannot keep.
/// </summary>
public sealed class BackupUi(
    EntryStore store,
    string imagesDirectory,
    Func<bool, string?> readSettingsJson,
    Func<string, string?> restoreSettingsJson)
{
    public void Export(Window owner)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出备份",
            Filter = "拾语备份 (*.shiyubk)|*.shiyubk",
            FileName = $"拾语备份 {DateTime.Now:yyyy-MM-dd}.shiyubk",
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        var password = AskPassword(owner, "为备份设置口令", withConfirm: true,
            accept: "加密导出", skip: "不加密导出");
        if (password.Cancelled)
        {
            return;
        }

        // ADR-0011：密钥默认不随备份离开本机；只有加密导出时用户可以勾选
        // 带上（明文形式，包内加密），导入后由本机的 DPAPI 重新保护。
        var settings = readSettingsJson(password.Bytes is not null && password.IncludeKey);
        RunWithProgress(owner, "正在导出…", report =>
        {
            var counts = BackupArchive.Export(
                dialog.FileName, store, imagesDirectory, settings, password.Bytes, report);
            return $"已导出 {counts.Entries} 条、{counts.Images} 张原图，共 {counts.Bytes / 1024} KB。";
        },
        summary => ContentDialog.Show(
            owner, "导出完成", summary, new ContentDialogButton("知道了")));
    }

    public void Import(Window owner)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入备份",
            Filter = "拾语备份 (*.shiyubk)|*.shiyubk",
        };
        if (dialog.ShowDialog(owner) != true)
        {
            return;
        }

        var password = AskPassword(owner, "备份的口令", withConfirm: false,
            accept: "用口令导入", skip: "无口令导入");
        if (password.Cancelled)
        {
            return;
        }

        var overwrite = AskImportMode(owner);
        if (overwrite is null)
        {
            return;
        }


        RunWithProgress(owner, "正在导入…", report =>
        {
            var outcome = BackupArchive.Import(
                dialog.FileName, store, imagesDirectory, overwrite.Value, password.Bytes, report);
            return (outcome, overwrite.Value);
        },
        result =>
        {
            var (outcome, overwrite) = result;
            var summary = overwrite
                ? $"覆盖完成：导入 {outcome.Added} 条、{outcome.ImagesRestored} 张原图、{outcome.GroupsCreated} 个分组。"
                : $"合并完成：新增 {outcome.Added} 条（{outcome.SkippedExisting} 条已有跳过），还原 {outcome.ImagesRestored} 张原图。";

            if (outcome.SettingsJson is { } settings)
            {
                // Applied here, on the UI thread, after the import landed.
                // The delegate answers null for applied, or the plain-words
                // reason it kept the current settings instead.
                string? refusal;
                try
                {
                    refusal = restoreSettingsJson(settings);
                }
                catch (Exception failure)
                {
                    // The import itself has landed; only the settings
                    // stumbled, and the summary must not claim otherwise.
                    Log.Event(LogEvent.BackupOperationFailed, failure, ("stage", 2));
                    refusal = "条目已导入，但设置恢复没有完成。";
                }

                if (refusal is { } because)
                {
                    summary += "\n" + because;
                }
            }

            ContentDialog.Show(owner, "导入完成", summary, new ContentDialogButton("知道了"));
        });
    }

    /// <summary>
    /// True = overwrite, false = merge, null = walked away.
    /// 覆盖导入按 §6.5 走 ContentDialog：不可撤销的动作说清后果，危险钮
    /// Danger 实底，默认焦点在取消上。
    /// </summary>
    private static bool? AskImportMode(Window owner)
    {
        var answer = ContentDialog.Show(
            owner,
            "导入方式",
            "合并：保留现有内容，只补进不重复的（按内容判断，不看时间）。\n覆盖：清空当前历史，换成备份里的内容（设置一并恢复）。",
            new ContentDialogButton("取消", ContentDialogButtonStyle.Standard, IsCancelFocus: true),
            new ContentDialogButton("合并导入"),
            new ContentDialogButton("覆盖导入", ContentDialogButtonStyle.Danger));

        return answer switch
        {
            1 => false,
            2 => true,
            _ => null,
        };
    }

    private sealed record PasswordAnswer(bool Cancelled, byte[]? Bytes, bool IncludeKey = false);

    /// <summary>
    /// 定期备份的口令（评审 2026-10-16 F-20）：问两遍，字节交给调用方保护后存起来、随即抹掉。
    /// Cancelled = 用户走开了；Bytes 为 null = 选了不加密。不问要不要带 API 密钥：定期备份一律不带。
    /// </summary>
    public static (bool Cancelled, byte[]? Bytes) AskAutoBackupPassword(Window owner)
    {
        var answer = AskPassword(owner, "定期备份的口令", withConfirm: true,
            accept: "加密备份", skip: "不加密", offerKey: false);
        return (answer.Cancelled, answer.Bytes);
    }

    /// <summary>
    /// The password ask. Bytes are lifted straight out of the SecureString as
    /// UTF-16; skip returns null bytes for an unsealed backup, and the confirm
    /// copy is cleared the moment it has been compared.
    /// </summary>
    private static PasswordAnswer AskPassword(
        Window owner, string title, bool withConfirm, string accept, string skip, bool? offerKey = null)
    {
        var window = new Window
        {
            Title = title,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = (Brush)Application.Current.FindResource("Brush.Background"),
            FontFamily = (FontFamily)Application.Current.FindResource("Font.Ui"),
            FontSize = (double)Application.Current.FindResource("Type.Body"),
        };
        window.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");

        var root = new StackPanel { Margin = new Thickness(14) };

        var box = new PasswordBox { Padding = new Thickness(4) };
        box.SetResourceReference(Control.BackgroundProperty, "Brush.SurfaceInput");
        root.Children.Add(box);

        PasswordBox? again = null;
        if (withConfirm)
        {
            again = new PasswordBox { Padding = new Thickness(4), Margin = new Thickness(0, 6, 0, 0) };
            again.SetResourceReference(Control.BackgroundProperty, "Brush.SurfaceInput");
            root.Children.Add(again);
        }

        // 只有加密导出才问（ADR-0011）：不加密的备份带密钥等于明文外带，
        // 没有勾选可言。
        CheckBox? includeKey = null;
        if (offerKey ?? withConfirm)
        {
            includeKey = new CheckBox
            {
                Content = "同时导出 API 密钥（保存在加密的备份内）",
                Margin = new Thickness(0, 10, 0, 0),
            };
            root.Children.Add(includeKey);
        }

        var hint = new TextBlock
        {
            Text = "两次输入不一致。",
            Foreground = (Brush)Application.Current.FindResource("Brush.Danger"),
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 6, 0, 0),
        };
        if (withConfirm)
        {
            root.Children.Add(hint);
        }

        byte[]? answer = null;
        var includeKeyValue = false;
        var decided = false;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };

        var ok = new Button { Content = accept, Padding = new Thickness(12, 4, 12, 4), Cursor = Cursors.Hand };
        ok.Click += (_, _) =>
        {
            var first = BytesOf(box.SecurePassword);
            if (first is null)
            {
                hint.Text = "口令不能为空——不需要加密请点「" + skip + "」。";
                hint.Visibility = Visibility.Visible;
                return;
            }

            var second = withConfirm ? BytesOf(again!.SecurePassword) : null;
            var same = !withConfirm || second is not null && first.SequenceEqual(second);
            if (second is not null)
            {
                Array.Clear(second);
            }

            if (!same)
            {
                Array.Clear(first);
                hint.Text = "两次输入不一致。";
                hint.Visibility = Visibility.Visible;
                return;
            }

            answer = first;
            includeKeyValue = includeKey?.IsChecked == true;
            decided = true;
            window.Close();
        };

        var plain = new Button { Content = skip, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand };
        plain.Click += (_, _) =>
        {
            answer = null;
            decided = true;
            window.Close();
        };

        var cancel = new Button { Content = "取消", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand };
        cancel.Click += (_, _) => window.Close();

        row.Children.Add(ok);
        row.Children.Add(plain);
        row.Children.Add(cancel);
        root.Children.Add(row);
        window.Content = root;
        window.ShowDialog();

        return decided
            ? new PasswordAnswer(false, answer, includeKeyValue)
            : new PasswordAnswer(true, null);
    }

    private static byte[]? BytesOf(SecureString secret)
    {
        if (secret.Length == 0)
        {
            return null;
        }

        var pinned = Marshal.SecureStringToCoTaskMemUnicode(secret);
        try
        {
            var bytes = new byte[secret.Length * 2];
            Marshal.Copy(pinned, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(pinned);
        }
    }

    /// <summary>
    /// Runs the work on the pool and hands its result to
    /// <paramref name="done"/> back on the UI thread — the await resumes on
    /// the dispatcher, so settings and windows may be touched from there and
    /// nowhere else.
    /// </summary>
    private static void RunWithProgress<T>(Window owner, string title, Func<IProgress<string>, T> work, Action<T> done)
    {
        var window = new Window
        {
            Title = title,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = (Brush)Application.Current.FindResource("Brush.Background"),
            FontFamily = (FontFamily)Application.Current.FindResource("Font.Ui"),
            FontSize = (double)Application.Current.FindResource("Type.Body"),
        };
        window.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");

        var root = new StackPanel { Margin = new Thickness(14) };
        var status = new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 8) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextSecondary");
        var bar = new ProgressBar { IsIndeterminate = true, Height = 6, Minimum = 0, Maximum = 100 };
        root.Children.Add(status);
        root.Children.Add(bar);
        window.Content = root;

        var progress = new Progress<string>(line => status.Text = line);
        window.Loaded += async (_, _) =>
        {
            try
            {
                var result = await Task.Run(() => work(progress));
                window.Close();
                done(result);
            }
            catch (BackupException failure)
            {
                window.Close();
                ContentDialog.Show(owner, "备份没有完成", failure.Message, new ContentDialogButton("知道了"));
            }
            catch (Exception failure)
            {
                // The import itself is transactional (O-03): whatever it
                // wrote, it wrote all of it or none. This path is the none,
                // so "nothing was touched" is the truth it says it is.
                Log.Event(LogEvent.BackupOperationFailed, failure);
                window.Close();
                ContentDialog.Show(
                    owner, "备份没有完成", "备份操作失败了，现有数据没有被动过。", new ContentDialogButton("知道了"));
            }
        };

        window.ShowDialog();
    }
}
