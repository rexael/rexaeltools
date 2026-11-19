using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

// GitHub Releases'teki en son sürümü kontrol eder; yenisi varsa indirir,
// çalışan exe'yi "FormatCarki.exe.old" adıyla kenara alıp yenisini yerine koyar ve yeniden başlatır.
// (Windows çalışan exe'nin üzerine yazmaya izin vermez ama ADINI değiştirmeye izin verir.)
// Her adım %TEMP%\FormatCarki.log dosyasına "[güncelleme]" önekiyle yazılır.
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

    // ---- Arayüzün okuduğu durum ----
    public static string Status { get; private set; } = "";
    public static bool Busy { get; private set; }
    public static event Action? Changed;

    // Tray tarafından doldurulur.
    public static string[] Args = Array.Empty<string>();
    public static Action<string>? Notify;
    public static Action? ExitApp;      // yeni sürüm başlatıldıktan sonra bu uygulamadan çıkar

    static void SetStatus(string s, bool busy)
    {
        Status = s; Busy = busy;
        try { Changed?.Invoke(); } catch { }
    }

    static void L(string s) => Tray.Log("[güncelleme] " + s);

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
            if (File.Exists(old)) { File.Delete(old); L("eski exe silindi: " + old); }
            var tmp = exe + ".new";
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        catch (Exception ex) { L("eski exe silinemedi: " + ex.Message); }
    }

    // manual=true: kullanıcı "Denetle"ye bastı; sonuç ekranda gösterilir.
    // Dönüş true ise yeni sürüm başlatıldı; çağıran uygulamadan çıkmalı (ExitApp bunu zaten yapar).
    public static async Task<bool> CheckAsync(bool manual)
    {
        if (Busy) { L("zaten çalışıyor, istek yok sayıldı"); return false; }
        SetStatus("Denetleniyor…", true);

        string? exe = Environment.ProcessPath;
        L($"başladı ({(manual ? "elle" : "açılışta")}): kurulu sürüm={Current}, exe={exe}, repo={Repo}");
        if (exe == null) { L("ProcessPath boş"); SetStatus("Hata: uygulama yolu okunamadı", false); return false; }

        string tmp = exe + ".new", old = exe + ".old";
        try
        {
            string api = $"https://api.github.com/repos/{Repo}/releases/latest";
            L("istek: " + api);

            using var resp = await Http.GetAsync(api);
            L($"cevap: {(int)resp.StatusCode} {resp.ReasonPhrase}");

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                L("404 = yayınlanmış (pre-release OLMAYAN) bir release yok, ya da repo private. " +
                  "Release 'Set as the latest release' ile yayınlanmalı, pre-release/draft olmamalı.");
                SetStatus("Release bulunamadı (404)", false);
                return false;
            }
            if (resp.StatusCode == HttpStatusCode.Forbidden || (int)resp.StatusCode == 429)
            {
                string rem = resp.Headers.TryGetValues("X-RateLimit-Remaining", out var v) ? string.Join(",", v) : "?";
                L($"{(int)resp.StatusCode}: GitHub istek sınırı olabilir (kalan hak: {rem}). Biraz sonra tekrar dene.");
                SetStatus("GitHub istek sınırı, sonra dene", false);
                return false;
            }
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            string rawTag = root.GetProperty("tag_name").GetString() ?? "";
            string tag = rawTag.TrimStart('v', 'V');
            bool pre = root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
            L($"son release: tag={rawTag}, prerelease={pre}");

            if (!Version.TryParse(tag, out var latest))
            {
                L($"tag sürüm olarak okunamadı: '{rawTag}' (v1.0.2 gibi olmalı)");
                SetStatus($"Tag okunamadı: {rawTag}", false);
                return false;
            }
            if (Norm(latest) <= Current)
            {
                L($"güncelleme yok (kurulu {Current}, GitHub'daki son {tag})");
                SetStatus($"Güncel (GitHub'daki son sürüm {tag})", false);
                return false;
            }

            string? url = null, digest = null;
            var names = new List<string>();
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                string n = a.GetProperty("name").GetString() ?? "";
                long size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : -1;
                names.Add($"{n} ({size} bayt)");
                if (n != AssetName) continue;
                url = a.GetProperty("browser_download_url").GetString();
                if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String) digest = d.GetString();
            }
            L("release dosyaları: " + (names.Count == 0 ? "(hiç dosya yok)" : string.Join(", ", names)));
            if (url == null)
            {
                L($"release'te '{AssetName}' adlı dosya yok. Dosya adı birebir bu olmalı.");
                SetStatus($"Release'te {AssetName} yok", false);
                return false;
            }

            SetStatus($"Yeni sürüm {tag} indiriliyor…", true);
            Notify?.Invoke($"Yeni sürüm {tag} indiriliyor…");
            L("indirme: " + url);
            using (var dl = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                L($"indirme cevabı: {(int)dl.StatusCode} {dl.ReasonPhrase}");
                dl.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await dl.Content.CopyToAsync(fs);
            }

            // Doğrulama: boyut + "MZ" (exe) başlığı + (varsa) GitHub'ın verdiği SHA-256
            var fi = new FileInfo(tmp);
            L($"indirilen boyut: {fi.Length} bayt");
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
                L("SHA-256 doğrulandı");
            }
            else L("GitHub SHA-256 vermedi, yalnızca boyut ve MZ başlığı doğrulandı");

            // Çalışan exe'yi kenara al, yenisini yerine koy (hata olursa geri al).
            if (File.Exists(old)) File.Delete(old);
            L($"exe yeniden adlandırılıyor: {exe} -> {old}");
            File.Move(exe, old);
            try { File.Move(tmp, exe); }
            catch (Exception mv) { L("yeni exe yerine konamadı, geri alınıyor: " + mv.Message); File.Move(old, exe); throw; }

            // Yeni sürüm, mutex'i bizden devralmak için kısa süre bekler (bkz. Main).
            var rest = Args.Where(a => a != "--updated").Append("--updated");
            Process.Start(new ProcessStartInfo(exe, string.Join(" ", rest)) { UseShellExecute = false });
            L("güncellendi → " + tag + ", yeni sürüm başlatıldı");
            SetStatus($"Güncellendi → {tag}", false);
            try { ExitApp?.Invoke(); } catch (Exception ex) { L("çıkış hatası: " + ex.Message); }
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            L("YAZMA İZNİ YOK (uygulama Program Files gibi korumalı bir klasörde olabilir): " + ex.Message);
            SetStatus("Klasöre yazma izni yok", false);
        }
        catch (HttpRequestException ex)
        {
            L($"ağ hatası: durum={(ex.StatusCode.HasValue ? ((int)ex.StatusCode.Value).ToString() : "yok")}, {ex.GetType().Name}: {ex.Message}" +
              (ex.InnerException != null ? " | iç: " + ex.InnerException.Message : ""));
            SetStatus(ex.StatusCode.HasValue ? $"Hata: HTTP {(int)ex.StatusCode.Value}" : "İnternet bağlantısı kurulamadı", false);
        }
        catch (TaskCanceledException)
        {
            L("zaman aşımı (3 dk) veya istek iptal edildi");
            SetStatus("Zaman aşımı", false);
        }
        catch (Exception ex)
        {
            L($"hata: {ex.GetType().Name}: {ex.Message}");
            SetStatus("Hata: " + ex.Message, false);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            if (Busy) SetStatus(Status, false);
        }
        return false;
    }
}