using SkiaSharp;
using System.Text;

static class Converter
{
    // Dönüştürür, yeni dosyanın yolunu döndürür. Görsel yalnızca bu sırada belleğe yüklenir.
    public static string Run(string src, string fmt, bool replace = false)
    {
        string dst = Unique(Path.ChangeExtension(src, fmt.ToLowerInvariant()));
        using var bmp = SKBitmap.Decode(src) ?? throw new InvalidDataException("Görsel okunamadı.");
        switch (fmt)
        {
            case "PNG": File.WriteAllBytes(dst, Enc(bmp, SKEncodedImageFormat.Png, 100, false)); break;
            case "JPG": File.WriteAllBytes(dst, Enc(bmp, SKEncodedImageFormat.Jpeg, 92, true)); break;
            case "WEBP": File.WriteAllBytes(dst, Enc(bmp, SKEncodedImageFormat.Webp, 90, false)); break;
            case "GIF": Gif(bmp, dst); break;
            case "PDF": File.WriteAllBytes(dst, Pdf(bmp)); break;
            case "SVG": File.WriteAllText(dst, Svg(bmp)); break;
        }
        // Yeni dosya gerçekten yazıldı mı? Değilse orijinale dokunma.
        var fi = new FileInfo(dst);
        Log($"yazıldı: {dst} var={fi.Exists} boyut={(fi.Exists ? fi.Length : -1)}");
        if (!fi.Exists || fi.Length == 0)
            throw new IOException("Yeni dosya oluşturulamadı; orijinal korundu.");

        if (replace)
        {
            string full = Path.GetFullPath(src);
            if (string.Equals(full, Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Hedef dosya orijinalle aynı; silme iptal edildi.");
            // Orijinal Geri Dönüşüm Kutusu'na gider (geri alınabilir).
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                Log($"orijinal geri dönüşüme gitti: {full}; yeni dosya hâlâ var={File.Exists(dst)}");
                Notify(full, deleted: true);
            }
            catch (Exception ex) { throw new IOException("Yeni dosya oluşturuldu ama orijinal silinemedi: " + ex.Message); }
        }
        Notify(dst, deleted: false);
        return dst;
    }

    // Masaüstü/Gezgin'e değişikliği bildirir; yenilemeye gerek kalmaz.
    const uint SHCNF_PATHW = 0x0005, SHCNF_FLUSH = 0x1000;
    const int SHCNE_CREATE = 0x2, SHCNE_DELETE = 0x4, SHCNE_UPDATEDIR = 0x1000;
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern void SHChangeNotify(int evt, uint flags, string? item1, string? item2);

    static void Notify(string path, bool deleted)
    {
        try
        {
            SHChangeNotify(deleted ? SHCNE_DELETE : SHCNE_CREATE, SHCNF_PATHW | SHCNF_FLUSH, path, null);
            string? dir = Path.GetDirectoryName(path);
            if (dir != null) SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSH, dir, null);
        }
        catch (Exception ex) { Log("bildirim hatası: " + ex.Message); }
    }

    static void Log(string m) => Tray.Log("Converter: " + m);

    static byte[] Enc(SKBitmap bmp, SKEncodedImageFormat f, int q, bool flatten)
    {
        if (!flatten)
        {
            using var im = SKImage.FromBitmap(bmp);
            using var d = im.Encode(f, q);
            return d.ToArray();
        }
        // JPG/PDF saydamlığı desteklemez: beyaz zemine yerleştir
        using var surf = SKSurface.Create(new SKImageInfo(bmp.Width, bmp.Height));
        surf.Canvas.Clear(SKColors.White);
        surf.Canvas.DrawBitmap(bmp, 0, 0);
        using var snap = surf.Snapshot();
        using var d2 = snap.Encode(f, q);
        return d2.ToArray();
    }

    // Skia GIF yazamaz; GIF için Windows'un kendi kodlayıcısı kullanılır.
    static void Gif(SKBitmap bmp, string dst)
    {
        using var ms = new MemoryStream(Enc(bmp, SKEncodedImageFormat.Png, 100, false));
        using var gi = System.Drawing.Image.FromStream(ms);
        gi.Save(dst, System.Drawing.Imaging.ImageFormat.Gif);
    }

    static string Unique(string p)
    {
        if (!File.Exists(p)) return p;
        string d = Path.GetDirectoryName(p)!, n = Path.GetFileNameWithoutExtension(p), e = Path.GetExtension(p);
        for (int i = 1; ; i++) { var c = Path.Combine(d, $"{n} ({i}){e}"); if (!File.Exists(c)) return c; }
    }

    // Not: gerçek vektörleştirme (izleme) yapmaz; görseli SVG içine gömer.
    static string Svg(SKBitmap bmp)
    {
        var png = Enc(bmp, SKEncodedImageFormat.Png, 100, false);
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{bmp.Width}\" height=\"{bmp.Height}\" viewBox=\"0 0 {bmp.Width} {bmp.Height}\">" +
               $"<image width=\"{bmp.Width}\" height=\"{bmp.Height}\" href=\"data:image/png;base64,{Convert.ToBase64String(png)}\"/></svg>";
    }

    static byte[] Pdf(SKBitmap bmp)
    {
        var jpg = Enc(bmp, SKEncodedImageFormat.Jpeg, 92, true);
        int w = bmp.Width, h = bmp.Height;
        string pw = (w * 0.75).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        string ph = (h * 0.75).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        using var o = new MemoryStream(); var off = new long[6];
        void T(string s) { var b = Encoding.ASCII.GetBytes(s); o.Write(b, 0, b.Length); }
        T("%PDF-1.4\n");
        off[1] = o.Position; T("1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n");
        off[2] = o.Position; T("2 0 obj\n<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj\n");
        off[3] = o.Position; T($"3 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 {pw} {ph}]/Resources<</XObject<</I 4 0 R>>>>/Contents 5 0 R>>\nendobj\n");
        off[4] = o.Position; T($"4 0 obj\n<</Type/XObject/Subtype/Image/Width {w}/Height {h}/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter/DCTDecode/Length {jpg.Length}>>\nstream\n");
        o.Write(jpg, 0, jpg.Length); T("\nendstream\nendobj\n");
        string cs = $"q {pw} 0 0 {ph} 0 0 cm /I Do Q";
        off[5] = o.Position; T($"5 0 obj\n<</Length {cs.Length}>>\nstream\n{cs}\nendstream\nendobj\n");
        long xr = o.Position; T("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) T($"{off[i]:D10} 00000 n \n");
        T($"trailer\n<</Size 6/Root 1 0 R>>\nstartxref\n{xr}\n%%EOF");
        return o.ToArray();
    }
}