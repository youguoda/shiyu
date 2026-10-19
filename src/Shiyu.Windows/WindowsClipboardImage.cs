using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shiyu.Core;

namespace Shiyu.Windows;

/// <summary>
/// A copied image as raw bytes, decoded and encoded on demand.
///
/// O-36's split: the clipboard can only be read on the message thread, and
/// the clipboard is about to change again — so that thread takes nothing but
/// the bytes, while the whole pixel pipeline (decode, thumbnail, encode,
/// fingerprint) runs in <see cref="RenderAsync"/> on the thread pool.
///
/// Applications that draw with alpha — WeChat, browsers, the snipping tool —
/// publish a PNG stream alongside the DIB, and WPF's DIB reading has
/// historically rendered those as solid black; the PNG bytes are therefore
/// taken first, and the DIB — wrapped with a bitmap file header, exactly the
/// transform WPF's own clipboard reader performs internally — is the
/// fallback.
/// </summary>
public sealed class WindowsClipboardImage : IClipboardImage
{
    /// <summary>
    /// Wide enough to recognise a screenshot at a glance in the list, small
    /// enough that thousands of them stay a database worth keeping forever.
    /// </summary>
    private const int ThumbnailWidth = 240;

    private const uint CfDib = 8;
    private const uint CfDibV5 = 17;

    private static readonly uint PngFormat =
        NativeMethods.RegisterClipboardFormatW("PNG");

    private readonly byte[] _raw;

    /// <summary>True when <see cref="_raw"/> is a PNG stream; false when it is a bare DIB.</summary>
    private readonly bool _isPng;

    private WindowsClipboardImage(byte[] raw, bool isPng)
    {
        _raw = raw;
        _isPng = isPng;
    }

    /// <summary>
    /// Takes the current clipboard image's bytes, or null if there is not
    /// one. Requires the clipboard to be already open — the caller holds it
    /// for every other format in the same open, so no second contention
    /// window is opened.
    /// </summary>
    public static WindowsClipboardImage? FromOpenClipboard()
    {
        // The PNG is the exact image and needs no interpretation; a real PNG
        // carries at least its eight-byte signature and header chunk.
        if (ReadBytes(PngFormat) is { Length: > 12 } png)
        {
            return new WindowsClipboardImage(png, isPng: true);
        }

        // V5 carries the alpha masks in the header; the legacy DIB follows.
        var dib = ReadBytes(CfDibV5) ?? ReadBytes(CfDib);
        return dib is { Length: > 40 } ? new WindowsClipboardImage(dib, isPng: false) : null;
    }

    /// <summary>Requires the clipboard to already be open.</summary>
    private static byte[]? ReadBytes(uint format)
    {
        var handle = NativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = (int)NativeMethods.GlobalSize(handle);
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return bytes;
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    public Task<RenderedImage> RenderAsync(CancellationToken cancellation = default)
        => Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested();

            // Decode and encode live on this one thread, so nothing frozen
            // ever crosses a thread boundary.
            var bitmap = Decode(_raw, _isPng);
            cancellation.ThrowIfCancellationRequested();

            return new RenderedImage(
                Encode(bitmap),
                Encode(Shrink(bitmap, ThumbnailWidth)),
                bitmap.PixelWidth,
                bitmap.PixelHeight,
                Fingerprint(bitmap));
        }, cancellation);

    private static BitmapSource Decode(byte[] raw, bool isPng)
    {
        using var stream = new MemoryStream(isPng ? raw : WrapDibWithFileHeader(raw));
        var decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        if (decoded.PixelWidth == 0 || decoded.PixelHeight == 0)
        {
            throw new InvalidDataException("clipboard bitmap decodes to nothing");
        }

        return decoded;
    }

    /// <summary>
    /// A DIB is a bitmap file without its fourteen-byte file header; putting
    /// the header back is the whole transform. The offset of the bits — the
    /// one number the header adds — is read out of the DIB's own header:
    /// header size, then the palette, then (for the legacy BITMAPINFOHEADER
    /// form of BI_BITFIELDS) the three channel masks that header has no room
    /// for.
    /// </summary>
    private static byte[] WrapDibWithFileHeader(byte[] dib)
    {
        var headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize < 40 || headerSize > dib.Length)
        {
            throw new InvalidDataException("unrecognised clipboard bitmap header");
        }

        var bitCount = BitConverter.ToUInt16(dib, 14);
        var compression = BitConverter.ToInt32(dib, 16);
        var coloursUsed = BitConverter.ToInt32(dib, 32);

        var palette = coloursUsed > 0
            ? coloursUsed * 4
            : bitCount <= 8 ? (1 << bitCount) * 4 : 0;

        // BITMAPV4/V5 headers carry their masks inside; only the 40-byte
        // BITMAPINFOHEADER with BI_BITFIELDS parks them just after itself.
        var masks = headerSize == 40 && compression == 3 ? 12 : 0;

        var offset = 14 + headerSize + palette + masks;

        var bitmap = new byte[14 + dib.Length];
        bitmap[0] = (byte)'B';
        bitmap[1] = (byte)'M';
        WriteUInt32(bitmap, 2, (uint)bitmap.Length);
        WriteUInt32(bitmap, 10, (uint)offset);
        dib.CopyTo(bitmap, 14);
        return bitmap;

        static void WriteUInt32(byte[] at, int position, uint value)
        {
            at[position] = (byte)value;
            at[position + 1] = (byte)(value >> 8);
            at[position + 2] = (byte)(value >> 16);
            at[position + 3] = (byte)(value >> 24);
        }
    }

    /// <summary>
    /// 把位图打包成剪贴板的 CF_DIB：BITMAPINFOHEADER（40 字节）+ 自下而上的 BGRA 像素——
    /// 读取方向 <see cref="WrapDibWithFileHeader"/> 的逆过程。32 位 BI_RGB，头里没有调色板
    /// 也没有掩码：贴回的接收端（本应用与绝大多数应用）都按这个最朴素的形状解。Bgra32 的
    /// 内存序本来就是 B、G、R、A，逐行倒序拷入即成。
    /// </summary>
    internal static byte[] BuildDib(BitmapSource bitmap)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        var dib = new byte[40 + pixels.Length];
        WriteInt32(dib, 0, 40);
        WriteInt32(dib, 4, width);
        WriteInt32(dib, 8, height);
        WriteInt16(dib, 12, 1);
        WriteInt16(dib, 14, 32);
        WriteInt32(dib, 20, pixels.Length);

        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(pixels, (height - 1 - y) * stride, dib, 40 + y * stride, stride);
        }

        return dib;

        static void WriteInt32(byte[] at, int position, int value)
        {
            at[position] = (byte)value;
            at[position + 1] = (byte)(value >> 8);
            at[position + 2] = (byte)(value >> 16);
            at[position + 3] = (byte)(value >> 24);
        }

        static void WriteInt16(byte[] at, int position, short value)
        {
            at[position] = (byte)value;
            at[position + 1] = (byte)(value >> 8);
        }
    }

    /// <summary>
    /// A fingerprint of the decoded pixels, used to recognise the same copy
    /// arriving twice — which it reliably does, because applications publish
    /// a bitmap in several clipboard formats in turn and each one raises a
    /// notification.
    ///
    /// Sampled rows rather than the whole bitmap: a full-screen screenshot is
    /// tens of megabytes, and allocating that on every copy to answer a
    /// yes-or-no question would be worse than the problem. Rows spread across
    /// the image make two genuinely different screenshots colliding far less
    /// likely than the duplicate this exists to catch.
    /// </summary>
    private static long Fingerprint(BitmapSource bitmap)
    {
        const int sampleRows = 16;
        var stride = (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;
        var row = new byte[stride];

        // FNV-1a: not cryptographic, and does not need to be.
        unchecked
        {
            var hash = 14695981039346656037UL;

            void Mix(long value)
            {
                for (var i = 0; i < 8; i++)
                {
                    hash = (hash ^ (byte)(value >> (i * 8))) * 1099511628211UL;
                }
            }

            Mix(bitmap.PixelWidth);
            Mix(bitmap.PixelHeight);

            var step = Math.Max(1, bitmap.PixelHeight / sampleRows);
            for (var y = 0; y < bitmap.PixelHeight; y += step)
            {
                bitmap.CopyPixels(new System.Windows.Int32Rect(0, y, bitmap.PixelWidth, 1), row, stride, 0);
                foreach (var value in row)
                {
                    hash = (hash ^ value) * 1099511628211UL;
                }
            }

            return (long)hash;
        }
    }

    private static BitmapSource Shrink(BitmapSource source, int targetWidth)
    {
        if (source.PixelWidth <= targetWidth)
        {
            return source;
        }

        var scale = (double)targetWidth / source.PixelWidth;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static byte[] Encode(BitmapSource bitmap)
    {
        // PNG rather than JPEG: screenshots are mostly text and flat colour,
        // where JPEG's artefacts land exactly on the parts worth reading.
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }
}
