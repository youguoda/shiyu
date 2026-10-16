using System.IO;
using System.Windows;
using Shiyu.Core;

namespace Shiyu.App;

/// <summary>
/// 导出的那一趟（评审 2026-10-16 F-19）：有选中就先问导出选中的还是全部，再选文件与格式（CSV / Markdown），
/// 在后台写完，说一句结果。剪贴板历史与翻译记录共用。
/// </summary>
internal static class ExportUi
{
    /// <param name="what">「剪贴板历史」或「翻译记录」：对话框标题与默认文件名用。</param>
    /// <param name="selectedCount">选中了几条；0 就不问，直接导出全部。</param>
    /// <param name="selection">选中的那些（列表顺序），在后台线程上取——全选上万条也不卡界面。</param>
    /// <param name="everything">全部，在后台线程上一页一页地读。</param>
    /// <param name="write">写进去，返回写了几条。</param>
    /// <param name="tell">结果的一句话，在界面线程上说。</param>
    public static async void Run<T>(
        Window owner,
        string what,
        int selectedCount,
        Func<IEnumerable<T>> selection,
        Func<IEnumerable<T>> everything,
        Func<TextWriter, IEnumerable<T>, ExportFormat, int> write,
        Action<string> tell)
    {
        // async void 逃出去的异常是进程级崩溃（O-05）：写文件的失败收成一句人话与一行日志。
        try
        {
            var onlySelected = false;
            if (selectedCount > 0)
            {
                var answer = ContentDialog.Show(
                    owner,
                    $"导出{what}",
                    $"导出选中的 {selectedCount} 条，还是全部？",
                    new ContentDialogButton("取消", ContentDialogButtonStyle.Standard, IsCancelFocus: true),
                    new ContentDialogButton($"选中的 {selectedCount} 条"),
                    new ContentDialogButton("全部"));
                if (answer is not (1 or 2))
                {
                    return;
                }

                onlySelected = answer == 1;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = $"导出{what}",
                Filter = ExportFile.Filter,
                FileName = ExportFile.DefaultName(what, DateTime.Now),
            };
            if (dialog.ShowDialog(owner) != true)
            {
                return;
            }

            var path = dialog.FileName;
            var format = ExportFile.FormatAt(dialog.FilterIndex);
            var written = await Task.Run(() => WriteFile(path, format, onlySelected ? selection : everything, write));
            tell($"已导出 {written} 条到 {Path.GetFileName(path)}。");
        }
        catch (Exception failure)
        {
            Log.Event(LogEvent.BackupOperationFailed, failure, ("export", 1));
            tell(failure is IOException or UnauthorizedAccessException
                ? "导出没能完成：写不进那个位置，换个文件夹再试。"
                : "导出没能完成，详情在日志里。");
        }
    }

    /// <summary>先写到旁边、写完再换上：中途出错不留半个文件，也不毁掉同名的旧文件。</summary>
    private static int WriteFile<T>(
        string path,
        ExportFormat format,
        Func<IEnumerable<T>> source,
        Func<TextWriter, IEnumerable<T>, ExportFormat, int> write)
    {
        var staging = path + ".partial";
        try
        {
            int count;
            using (var writer = new StreamWriter(staging, append: false, ExportFile.EncodingFor(format)))
            {
                count = write(writer, source(), format);
            }

            File.Move(staging, path, overwrite: true);
            return count;
        }
        catch
        {
            try
            {
                File.Delete(staging);
            }
            catch (IOException)
            {
                // expected: 删不掉的半截文件留着，不掩盖真正的失败——下面原样抛出去。
            }

            throw;
        }
    }
}
