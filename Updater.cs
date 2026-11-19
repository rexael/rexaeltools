using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

// Açılışta GitHub Releases'teki en son sürümü kontrol eder; yenisi varsa indirir,
// çalışan exe'yi "FormatCarki.exe.old" adıyla kenara alıp yenisini yerine koyar ve yeniden başlatır.
// (Windows çalışan exe'nin üzerine yazmaya izin vermez ama ADINI değiştirmeye izin verir.)
static class Updater
{
    // >>> KENDİ GITHUB REPONA GÖRE DEĞİŞTİR: "kullanici/repo" <<<
    const string Repo = "rexael/rexaeltools";
    // Release'e yüklediğin dosyanın adı (dotnet publish çıktısı)
    const string AssetName = "FormatCarki.exe";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("FormatCarki-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    // 1.0.1 ile 1.0.1.0 eşit sayılsın (Version sınıfı aksi halde 1.0.1 < 1.0.1.0 der).
    static Version Norm(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    public static Version Current =>
        Norm(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));

    // Önceki güncellemeden kalan eski exe'yi siler.
    public static void CleanOld()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            var tmp = exe + ".new";
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        catch { }
    }

    // true dönerse yeni sürüm başlatıldı; çağıran uygulamadan çıkmalı.
    public static async Task<bool> TryUpdateAsync(string[] args, Action<string>? notify = null)
    {
        string? exe = Environment.ProcessPath;
        if (exe == null) return false;
        string tmp = exe + ".new", old = exe + ".old";
        try
        {
            using var doc = JsonDocument.Parse(
                await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            var root = doc.RootElement;

            string tag = root.GetProperty("tag_name").GetString()!.TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var latest) || Norm(latest) <= Current)
            {
                Tray.Log($"güncelleme yok (mevcut {Current}, son {tag})");
                return false;
            }

            string? url = null, digest = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                if (a.GetProperty("name").GetString() != AssetName) continue;
                url = a.GetProperty("browser_download_url").GetString();
                if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String) digest = d.GetString();
                break;
            }
            if (url == null) { Tray.Log("release'te " + AssetName + " yok"); return false; }

            notify?.Invoke($"Yeni sürüm {tag} indiriliyor…");
            using (var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs);
            }

            // Doğrulama: boyut + "MZ" (exe) başlığı + (varsa) GitHub'ın verdiği SHA-256
            var fi = new FileInfo(tmp);
            if (fi.Length < 100_000) throw new InvalidDataException("indirilen dosya çok küçük");
            using (var fs = File.OpenRead(tmp))
            {
                if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') throw new InvalidDataException("exe değil");
            }
            if (digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = File.OpenRead(tmp);
                string got = Convert.ToHexString(await SHA256.HashDataAsync(fs));
                if (!got.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 uyuşmuyor");
            }

            // Çalışan exe'yi kenara al, yenisini yerine koy (hata olursa geri al).
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try { File.Move(tmp, exe); }
            catch { File.Move(old, exe); throw; }

            // Yeni sürüm, mutex'i bizden devralmak için kısa süre bekler (bkz. Main).
            var rest = args.Where(a => a != "--updated").Append("--updated");
            Process.Start(new ProcessStartInfo(exe, string.Join(" ", rest)) { UseShellExecute = false });
            Tray.Log("güncellendi → " + tag);
            return true;
        }
        catch (Exception ex)
        {
            Tray.Log("güncelleme hatası: " + ex.Message);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }
}