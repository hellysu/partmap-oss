using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace PartMap.Services;

public sealed class ThumbnailService
{
    private readonly ConcurrentDictionary<string, ThumbnailCacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<BitmapSource?> LoadAsync(string path, int size = 160) => Task.Run(() =>
    {
        var fullPath = Path.GetFullPath(path);
        FileInfo? info = null;
        try
        {
            info = new FileInfo(fullPath);
            if (_cache.TryGetValue(fullPath, out var cached) &&
                cached.Size == size && cached.Length == info.Length && cached.LastWriteUtc == info.LastWriteTimeUtc)
            {
                return cached.Thumbnail;
            }
        }
        catch
        {
            info = null;
        }

        var thumbnail = string.Equals(Path.GetExtension(fullPath), ".3mf", StringComparison.OrdinalIgnoreCase)
            ? LoadEmbeddedThumbnail(fullPath)
            : LoadShellThumbnail(fullPath, size) ?? LoadEmbeddedThumbnail(fullPath);
        if (info is not null)
        {
            _cache[fullPath] = new ThumbnailCacheEntry(info.Length, info.LastWriteTimeUtc, size, thumbnail);
        }
        return thumbnail;
    });

    private static BitmapSource? LoadShellThumbnail(string path, int size)
    {
        IntPtr bitmapHandle = IntPtr.Zero;
        try
        {
            var interfaceId = typeof(IShellItemImageFactory).GUID;
            var result = SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out var factory);
            if (result != 0 || factory is null)
            {
                return null;
            }

            factory.GetImage(new NativeSize(size, size),
                ShellItemImageFlags.ThumbnailOnly | ShellItemImageFlags.BiggerSizeOk,
                out bitmapHandle);
            if (bitmapHandle == IntPtr.Zero)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }
        }
    }

    private static BitmapSource? LoadEmbeddedThumbnail(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
            var entry = archive.Entries
                .Where(item => item.Length > 0 &&
                               (item.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                item.FullName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                item.FullName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(item => item.FullName.Contains("thumbnail", StringComparison.OrdinalIgnoreCase))
                .ThenBy(item => item.FullName.Length)
                .FirstOrDefault();
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 200;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private sealed record ThumbnailCacheEntry(long Length, DateTime LastWriteUtc, int Size, BitmapSource? Thumbnail);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [Flags]
    private enum ShellItemImageFlags
    {
        BiggerSizeOk = 0x1,
        ThumbnailOnly = 0x8
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(NativeSize size, ShellItemImageFlags flags, out IntPtr bitmapHandle);
    }
}
