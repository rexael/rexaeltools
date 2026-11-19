using System.Drawing.Text;
using System.Runtime.InteropServices;

// Uygulamanın yazı tipi: gömülü Inter (Fonts\*.ttf, OFL lisanslı). Yüklenemezse Segoe UI'ye düşer.
// Boyutlar PİKSEL cinsindendir: ölçekleme (DPI) zaten Graphics.ScaleTransform ile yapılıyor;
// punto kullanmak yüksek DPI'da yazıyı iki kez büyütür.
static class UiFont
{
    static readonly PrivateFontCollection Pfc = new();
    static readonly List<IntPtr> Mem = new();   // AddMemoryFont verisi uygulama boyunca bellekte kalmalı
    static readonly FontFamily? Regular_, Semi_;

    static UiFont()
    {
        try
        {
            Load("Inter-Regular.ttf");
            Load("Inter-SemiBold.ttf");
            Regular_ = Pfc.Families.FirstOrDefault(f => !f.Name.Contains("Semi"));
            Semi_ = Pfc.Families.FirstOrDefault(f => f.Name.Contains("Semi"));
        }
        catch (Exception ex) { Tray.Log("UiFont: yüklenemedi: " + ex.Message); }
        Tray.Log("UiFont: gömülü=" + Embedded + " aileler=[" + string.Join(", ", Pfc.Families.Select(f => f.Name)) + "]");
    }

    public static bool Embedded => Regular_ != null && Semi_ != null;

    // Inter (düzensiz/donanımsız çizim) gri tonlamada, yedek Segoe UI (donanımlı) ClearType'ta en temiz görünür.
    public static TextRenderingHint Hint => Embedded ? TextRenderingHint.AntiAlias : TextRenderingHint.ClearTypeGridFit;

    static void Load(string resource)
    {
        byte[] data;
        using (var s = typeof(UiFont).Assembly.GetManifestResourceStream(resource))
        {
            if (s != null) { data = new byte[s.Length]; s.ReadExactly(data); }
            else
            {
                // Kaynak gömülmemişse exe'nin yanındaki Fonts klasörüne bak.
                var file = Path.Combine(AppContext.BaseDirectory, "Fonts", resource);
                Tray.Log("UiFont: gömülü kaynak yok (" + resource + "); kaynaklar=[" +
                    string.Join(", ", typeof(UiFont).Assembly.GetManifestResourceNames()) + "]; dosya aranıyor: " + file);
                if (!File.Exists(file)) return;
                data = File.ReadAllBytes(file);
            }
        }
        var p = Marshal.AllocCoTaskMem(data.Length);
        Marshal.Copy(data, 0, p, data.Length);
        Pfc.AddMemoryFont(p, data.Length);
        Mem.Add(p);
    }

    public static Font Regular(float px) => Regular_ != null
        ? new Font(Regular_, px, FontStyle.Regular, GraphicsUnit.Pixel)
        : new Font("Segoe UI", px, FontStyle.Regular, GraphicsUnit.Pixel);

    public static Font Semi(float px) => Semi_ != null
        ? new Font(Semi_, px, FontStyle.Regular, GraphicsUnit.Pixel)
        : new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel);

    // Aynı yerleşim için ortak biçim: GDI+'nın varsayılan iç boşlukları kapalı, satır sınırlı.
    public static StringFormat Format(StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Near)
        => new(StringFormat.GenericTypographic)
        {
            Alignment = h,
            LineAlignment = v,
            Trimming = StringTrimming.EllipsisCharacter
        };
}