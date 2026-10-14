using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace LeafCalendar.App.Api;

/// <summary>
/// Images for the local API's approval window and Settings › Connected apps: a program file's own icon (the shell's
/// thumbnail of the file, as File Explorer shows it) and an app's PNG. Both are made on the UI thread and never throw:
/// a file that's gone, or bytes Windows can't decode, give null and the caller shows the generic app icon instead.
/// </summary>
internal static class ApiIcons
{
    /// <summary>The program's icon at about <paramref name="size"/> pixels, or null.</summary>
    public static async Task<ImageSource?> ProgramAsync(string path, uint size)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, size, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null)
            {
                return null;
            }

            var image = new BitmapImage();
            await image.SetSourceAsync(thumbnail);
            return image;
        }
        catch (Exception)
        {
            // A missing file, no access, or no thumbnail: Windows reports each as a different exception or HRESULT
            return null;
        }
    }

    /// <summary>An app's PNG (already checked by <see cref="Core.Api.ApiHello.ReadIcon"/>) as an image, or null when Windows can't decode it.</summary>
    public static async Task<ImageSource?> PngAsync(ReadOnlyMemory<byte> png)
    {
        if (png.IsEmpty)
        {
            return null;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.ToArray().AsBuffer());
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            return image;
        }
        catch (Exception)
        {
            // Bytes that pass the header check but aren't a whole PNG: the decoder's HRESULT varies
            return null;
        }
    }
}
