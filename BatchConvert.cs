using System.Diagnostics;
using System.IO.Compression;

// Toplu Dönüştür: dosya listesi, format seçimi, dönüştürme ve sonuç klasörü işleri burada.
// MainForm yalnızca çizim yapar ve bu sınıfı çağırır. Araç her zaman kullanılabilir (aç/kapa yok).
static class BatchConvert
{
    public const string Id = "batch-convert";
    public static readonly string[] Formats = { "PNG", "JPG", "WEBP", "GIF", "PDF", "SVG" };
    static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };

    // Tüm durum UI iş parçacığında değiştirilir.
    public static readonly List<string> Files = new();
    public static int Format { get; set; }
    public static bool Zip { get; set; }
    public static bool Running { get; private set; }
    public static int Done { get; private set; }
    public static int Total { get; private set; }
    public static string Status { get; private set; } = "";

    public static event Action? Changed;
    static void Notify() => Changed?.Invoke();

    static bool IsImg(string p) => Exts.Contains(Path.GetExtension(p).ToLowerInvariant());

    // ---------- Liste yönetimi ----------
    public static void AddFiles(IEnumerable<string> paths)
    {
        if (Running) return;
        int added = 0;
        foreach (var p in paths)
        {
            try
            {
                var list = Directory.Exists(p) ? Directory.EnumerateFiles(p).ToArray() : new[] { p };
                foreach (var f in list)
                    if (File.Exists(f) && IsImg(f) && !Files.Contains(f, StringComparer.OrdinalIgnoreCase))
                    { Files.Add(f); added++; }
            }
            catch (Exception ex) { Tray.Log("Dosya eklenemedi: " + ex.Message); }
        }
        // Yeni bir tur başlıyor: eski ilerleme ve durum temizlenir.
        Done = Total = 0;
        Status = added == 0 ? "Görsel bulunamadı" : "";
        Notify();
    }

    public static void AddDialog(IWin32Window owner)
    {
        if (Running) return;
        using var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Fotoğrafları seç",
            Filter = "Görseller|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|Tüm dosyalar|*.*"
        };
        if (dlg.ShowDialog(owner) == DialogResult.OK) AddFiles(dlg.FileNames);
    }

    public static void Clear()
    {
        if (Running) return;
        Files.Clear(); Done = Total = 0; Status = "";
        Notify();
    }

    // ---------- Dönüştürme ----------
    record Result(int Ok, int Same, List<string> Failed, string? Target);

    public static async void Run()
    {
        if (Running || Files.Count == 0) return;

        var files = Files.ToList();
        string fmt = Formats[Format];
        bool zip = Zip;
        string outDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Rexael Tools",
            DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss") + " " + fmt);
        var ui = SynchronizationContext.Current;

        Running = true; Done = 0; Total = files.Count; Status = "Başlıyor…";
        Notify();

        Result? res = null;
        try
        {
            res = await Task.Run(() => Convert(files, fmt, outDir, zip, n =>
                ui?.Post(_ => { if (!Running) return; Done = n; Status = $"{n}/{files.Count} • {fmt}"; Notify(); }, null)));
        }
        catch (Exception ex)
        {
            Tray.Log("Toplu dönüştürme çöktü: " + ex.Message);
        }

        // Ne olursa olsun kilit açılır; böylece yeni dosya ekleyip tekrar dönüştürülebilir.
        Running = false;
        if (res == null)
        {
            Status = "Hata oluştu";
        }
        else
        {
            int good = res.Ok + res.Same;
            Status = res.Failed.Count == 0 ? $"Bitti • {good} dosya" : $"{good} tamam • {res.Failed.Count} hata";
            // Başarılılar listeden çıkar; hatalılar kalır ki tekrar denenebilsin.
            Files.Clear();
            Files.AddRange(res.Failed);
            if (good > 0 && res.Target != null) OpenResult(res.Target);
        }
        Notify();
    }

    // Her dosyayı geçici klasörde kopyalayıp Converter.Run ile dönüştürür, çıktıyı hedef klasöre toplar.
    // Orijinal dosyalara dokunulmaz.
    static Result Convert(List<string> files, string fmt, string outDir, bool zip, Action<int> progress)
    {
        Directory.CreateDirectory(outDir);
        string work = Path.Combine(Path.GetTempPath(), "FormatCarki-batch-" + Guid.NewGuid().ToString("N"));
        int ok = 0, same = 0, i = 0;
        var failed = new List<string>();
        try
        {
            foreach (var f in files)
            {
                i++;
                try
                {
                    if (SameFmt(f, fmt))
                    {
                        File.Copy(f, Unique(outDir, Path.GetFileName(f)));
                        same++;
                    }
                    else
                    {
                        string dir = Path.Combine(work, i.ToString());
                        Directory.CreateDirectory(dir);
                        string copy = Path.Combine(dir, Path.GetFileName(f));
                        File.Copy(f, copy, true);
                        Converter.Run(copy, fmt, false);
                        var made = Directory.GetFiles(dir)
                            .Where(x => !string.Equals(x, copy, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (made.Count == 0) throw new Exception("Çıktı oluşmadı");
                        foreach (var m in made) File.Move(m, Unique(outDir, Path.GetFileName(m)));
                        ok++;
                    }
                }
                catch (Exception ex)
                {
                    failed.Add(f);
                    Tray.Log("Toplu dönüştürme hatası (" + Path.GetFileName(f) + "): " + ex.Message);
                }
                progress(i);
            }

            if (ok + same == 0)
            {
                try { Directory.Delete(outDir); } catch { }
                return new Result(ok, same, failed, null);
            }

            string target = outDir;
            if (zip)
            {
                string zp = outDir + ".zip";
                if (File.Exists(zp)) File.Delete(zp);
                ZipFile.CreateFromDirectory(outDir, zp);
                Directory.Delete(outDir, true);
                target = zp;
            }
            return new Result(ok, same, failed, target);
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }
    }

    static bool SameFmt(string path, string fmt)
    {
        string e = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (e == "JPEG") e = "JPG";
        return e == fmt;
    }

    static string Unique(string dir, string name)
    {
        string p = Path.Combine(dir, name);
        if (!File.Exists(p)) return p;
        string b = Path.GetFileNameWithoutExtension(name), e = Path.GetExtension(name);
        for (int i = 2; ; i++)
        {
            p = Path.Combine(dir, $"{b} ({i}){e}");
            if (!File.Exists(p)) return p;
        }
    }

    static void OpenResult(string target)
    {
        try
        {
            if (File.Exists(target)) Process.Start("explorer.exe", $"/select,\"{target}\"");
            else Process.Start("explorer.exe", $"\"{target}\"");
        }
        catch (Exception ex) { Tray.Log("Klasör açılamadı: " + ex.Message); }
    }
}