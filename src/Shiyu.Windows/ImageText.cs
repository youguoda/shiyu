using System.Runtime.InteropServices.WindowsRuntime;
using Shiyu.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Shiyu.Windows;

/// <summary>
/// 离线文字识别（评审 2026-10-16 F-07）：<c>Windows.Media.Ocr</c>，按用户的系统语言挑识别器（中文系统认中文，
/// 也认拉丁字母），不联网。解码、识别全在调用方的后台线程上，不碰界面线程。超过识别器上限的大图先等比缩小。
/// 没有可用的识别语言、图坏了，都只是 null——识别是锦上添花，失败不该打扰谁。
/// </summary>
public static class ImageText
{
    private static readonly Lazy<OcrEngine?> Engine = new(() =>
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(engine => engine is not null);
        }
        catch (Exception)
        {
            // expected: 精简版系统没有识别组件——当作没有。
            return null;
        }
    });

    /// <summary>这台机器能不能识别（有没有可用的识别语言）。</summary>
    public static bool Available => Engine.Value is not null;

    /// <summary>识别一张图（PNG / JPEG / BMP 字节）里的文字：一行一行；没有字是空串，做不了是 null。</summary>
    public static async Task<string?> RecognizeAsync(byte[] image)
    {
        if (Engine.Value is not { } engine || image.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(image.AsBuffer());
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform();
            var longest = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            if (longest > OcrEngine.MaxImageDimension)
            {
                var scale = (double)OcrEngine.MaxImageDimension / longest;
                transform.ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale);
                transform.ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale);
            }

            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
            var result = await engine.RecognizeAsync(bitmap);
            return OcrText.JoinLines(result.Lines.Select(line => OcrText.JoinWords(line.Words.Select(word => word.Text))));
        }
        catch (Exception failure)
        {
            // 图坏了、格式不认：留一行日志（不含内容），照契约给 null。
            Log.Event(LogEvent.OcrFailed, failure);
            return null;
        }
    }
}
