using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace GenZPOS.Services;

/// <summary>
/// Prepares the shop logo for receipts and invoices: any PNG / JPG / BMP / GIF becomes a PNG at most 576 px wide
/// (= 72 mm at 203 dpi, the printable width of an 80 mm thermal roll) and 576 px high, on a white background
/// (thermal printers can't print transparency). Stored base64 in the Settings table (ShopInfo.Logo).
/// </summary>
public static class LogoImage
{
    public const string FileFilter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif";
    private const int MaxPixels = 576;
    private const long MaxFileBytes = 10 * 1024 * 1024;

    /// <summary>The prepared PNG bytes, or a <see cref="BusinessException"/> when the file isn't a usable image.</summary>
    public static byte[] Prepare(Stream file)
    {
        if (file.Length > MaxFileBytes)
            throw new BusinessException("The logo file is larger than 10 MB. Save it smaller and try again.");
        Image source;
        try
        {
            source = Image.FromStream(file);
        }
        catch (Exception)
        {
            throw new BusinessException("This file can't be read as an image. Use a PNG or JPG file.");
        }

        using (source)
        {
            var scale = Math.Min(1.0, Math.Min((double)MaxPixels / source.Width, (double)MaxPixels / source.Height));
            var w = Math.Max(1, (int)Math.Round(source.Width * scale));
            var h = Math.Max(1, (int)Math.Round(source.Height * scale));
            using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, w, h));
            }
            using var output = new MemoryStream();
            bmp.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
    }
}
