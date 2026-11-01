using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RCL.Core;

/// <summary>
/// Loads a cached avatar into a frozen brush. Reading through a stream instead of UriSource
/// bypasses WPF's URI image cache, so a freshly downloaded picture replaces the old one at once.
/// </summary>
public static class AvatarBrush
{
    public static ImageBrush? Load(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            return brush;
        }
        catch (Exception ex)
        {
            Log.Warn("加载头像失败：" + ex.Message);
            return null;
        }
    }
}
