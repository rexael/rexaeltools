using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Accessibility;
using Microsoft.Win32;

record Tool(
    string Id,
    string Name,
    string Desc,
    string Cat,
    Action<bool> Apply,
    bool DefOn = true            // ilk kurulumda açık mı?
);

static class Store
{
    const string K = @"Software\RexaelTools\Tools";

    public static bool Get(string id, bool def = true)
    {
        return Registry.GetValue(
            "HKEY_CURRENT_USER\\" + K,
            id,
            def ? 1 : 0
        ) is int v ? v != 0 : def;
    }

    public static int GetInt(string name, int def) =>
        Registry.GetValue("HKEY_CURRENT_USER\\" + K, name, def) is int v ? v : def;

    public static void SetInt(string name, int v)
    {
        using var k = Registry.CurrentUser.CreateSubKey(K);
        k.SetValue(name, v, RegistryValueKind.DWord);
    }

    public static void Set(string id, bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(K);
        k.SetValue(id, on ? 1 : 0, RegistryValueKind.DWord);
    }

    // Uygulama ayarları (araç aç/kapa değerleriyle aynı anahtarda saklanır)
    public const string ReplaceKey = "format-carki.replace";

    public static bool GetOpt(string name, bool def)
    {
        return Registry.GetValue("HKEY_CURRENT_USER\\" + K, name, def ? 1 : 0) is int v ? v != 0 : def;
    }

    public static void SetOpt(string name, bool on) => Set(name, on);
}

// "Windows ile başlat" — hem tepsi menüsü hem ana pencere aynı kaydı kullanır.
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "FormatCarki";

    public static bool Get() => Registry.GetValue("HKEY_CURRENT_USER\\" + RunKey, Name, null) != null;

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{Application.ExecutablePath}\" --tray");
        else k.DeleteValue(Name, false);
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Güncelleme sonrası yeniden başlatılan kopya, eski kopyanın kapanmasını birkaç sn bekler.
        using var mtx = new Mutex(false, "FormatCarki.Single");
        bool first;
        try { first = mtx.WaitOne(args.Contains("--updated") ? 10000 : 0); }
        catch (AbandonedMutexException) { first = true; }
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new Tray(args));
    }
}

// Boştayken tek yaptığı şey: klavye kancası. Zamanlayıcı/döngü yok; CPU ≈ 0.
class Tray : ApplicationContext
{
    const int HoldMs = 400;                 // Ctrl bu kadar basılı kalınca çark açılır
    delegate IntPtr HookProc(int n, IntPtr w, IntPtr l);
    readonly HookProc _proc;
    IntPtr _hook;
    readonly List<Tool> Tools;
    MainForm? _main;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _hold = new() { Interval = HoldMs };
    readonly System.Windows.Forms.Timer _commit = new() { Interval = 1 };
    Wheel? _wheel;
    bool _ctrl, _cancel;

    public Tray(string[] args)
    {
        var auto = new ToolStripMenuItem("Windows ile başlat") { Checked = Autostart.Get() };
        auto.Click += (_, _) => { try { Autostart.Set(!Autostart.Get()); } catch (Exception ex) { Log(ex.Message); } };
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => auto.Checked = Autostart.Get(); // uygulamadan değiştirilmiş olabilir
        menu.Items.Add("Rexael Tools’u aç", null, (_, _) => ShowMain());
        menu.Items.Add(auto);
        menu.Items.Add("Çık", null, (_, _) => { _tray!.Visible = false; Application.Exit(); });
        _tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Rexael Tools", ContextMenuStrip = menu, Visible = true };
        AutoClicker.Changed += () => _tray.Text = AutoClicker.Running ? "Rexael Tools • Tıklıyor" : "Rexael Tools";

        _tray.DoubleClick += (_, _) => ShowMain();
        _hold.Tick += (_, _) => OnHold();
        _commit.Tick += (_, _) => { _commit.Stop(); _wheel?.Finish(_cancel); Trim(); };
        _proc = Hook;
        Tools = new()
        {
            new("format-carki", "Format Çarkı", "Gezgin’de bir görselin üstünde Ctrl’yi basılı tut; PNG, JPG, WEBP, GIF, PDF veya SVG’ye dönüştür.", "Uygulamalar", SetWheel),
            new(AutoClicker.Id, "AutoClicker", "Kısayola basınca imlecin olduğu yerde ayarladığın aralıkla tıklar; tekrar basınca durur.", "Uygulamalar", AutoClicker.Apply, false),
            new("batch-convert", "Toplu Dönüştür", "Birçok fotoğrafı sürükle-bırak yap, istediğin formata tek seferde dönüştürüp bir klasöre kaydeder.", "Eklentiler", _ => { })
        };
        foreach (var t in Tools) { try { t.Apply(Store.Get(t.Id, t.DefOn)); } catch (Exception ex) { Log(ex.Message); } }
        if (!args.Contains("--tray")) ShowMain();
        Trim();

        // Açılışta arka planda güncelleme denetimi: yeni sürüm varsa indirir, değiştirir, yeniden başlatır.
        Updater.CleanOld();
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000); // açılışı yavaşlatmasın
            if (await Updater.TryUpdateAsync(args, m => _tray.ShowBalloonTip(3000, "Rexael Tools", m, ToolTipIcon.Info)))
            {
                _tray.Visible = false;
                Application.Exit();
            }
        });
    }

    // Kapalıyken kanca tamamen kaldırılır: işlemci ve bellek kullanımı sıfır.
    void SetWheel(bool on)
    {
        if (on && _hook == IntPtr.Zero)
        {
            _hook = SetWindowsHookEx(13, _proc, GetModuleHandle(Process.GetCurrentProcess().MainModule!.ModuleName), 0);
            Log(_hook == IntPtr.Zero ? "KANCA KURULAMADI, hata=" + Marshal.GetLastWin32Error() : "kanca kuruldu");
        }
        else if (!on && _hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; _hold.Stop(); _ctrl = false;
            _wheel?.Finish(true); _wheel?.Dispose(); _wheel = null; Trim();
        }
    }

    void ShowMain()
    {
        if (_main != null && !_main.IsDisposed) { _main.Activate(); return; }
        _main = new MainForm(Tools);
        _main.FormClosed += (_, _) => { _main = null; Trim(); };
        _main.Show(); _main.Activate();
    }

    public static void Log(string s)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "FormatCarki.log"), $"{DateTime.Now:HH:mm:ss} {s}\n"); } catch { }
    }

    IntPtr Hook(int n, IntPtr w, IntPtr l)
    {
        if (n >= 0)
        {
            int vk = Marshal.ReadInt32(l), m = (int)w;
            bool down = m == 0x100 || m == 0x104, up = m == 0x101 || m == 0x105, ctrl = vk == 0xA2 || vk == 0xA3;
            bool open = _wheel?.Visible == true;
            if (ctrl && down) { if (!_ctrl) { _ctrl = true; _hold.Start(); } }
            else if (ctrl && up) { _ctrl = false; _hold.Stop(); if (open) { _cancel = false; _commit.Start(); } }
            else if (down) { _hold.Stop(); if (open) { _cancel = true; _commit.Start(); } } // başka tuş/Esc: iptal
        }
        return CallNextHookEx(IntPtr.Zero, n, w, l);
    }

    void OnHold()
    {
        _hold.Stop();
        Log("Ctrl basılı tutuldu, kontrol ediliyor");
        if (!_ctrl || (GetAsyncKeyState(1) & 0x8000) != 0) return; // Ctrl+tık (çoklu seçim) ise açma
        GetCursorPos(out var p);
        string? path = ImageUnderCursor(p);
        Log("bulunan dosya: " + (path ?? "yok"));
        if (path == null) return;
        _wheel ??= new Wheel();
        _wheel.Open(path, new Point(p.X, p.Y), Pick);
    }

    void Pick(string path, string fmt)
    {
        if (string.Equals(Path.GetExtension(path).TrimStart('.'), fmt, StringComparison.OrdinalIgnoreCase)
            || (fmt == "JPG" && Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase)))
        { _tray.ShowBalloonTip(2000, "Format çarkı", "Dosya zaten bu formatta.", ToolTipIcon.Info); return; }
        bool replace = Store.GetOpt(Store.ReplaceKey, false);
        Task.Run(() =>
        {
            try { Converter.Run(path, fmt, replace); } // başarılıysa bildirim yok
            catch (Exception ex) { _tray.ShowBalloonTip(4000, "Dönüştürülemedi", ex.Message, ToolTipIcon.Error); }
            finally { GC.Collect(); Trim(); }
        });
    }

    static readonly string[] Exts = { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };
    static bool IsImg(string p) => Exts.Contains(Path.GetExtension(p).ToLowerInvariant());

    // İmlecin altındaki Gezgin/masaüstü öğesini bulur; yalnızca çark tetiklenince çalışır.
    static string? ImageUnderCursor(POINT p)
    {
        try
        {
            var root = GetAncestor(WindowFromPoint(p), 2);
            var sb = new System.Text.StringBuilder(64); GetClassName(root, sb, 64);
            string cls = sb.ToString();
            AccessibleObjectFromPoint(p, out IAccessible acc, out object child);
            string? name = acc?.get_accName(child);
            Log($"pencere={cls} öğe adı={name}");
            if (string.IsNullOrEmpty(name)) return null;
            bool Match(string path) => IsImg(path) &&
                (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase));
            if (cls == "Progman" || cls == "WorkerW")
            {
                foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
                    if (Directory.Exists(dir))
                        foreach (var f in Directory.EnumerateFiles(dir)) if (Match(f)) return f;
            }
            else if (cls == "CabinetWClass")
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                foreach (dynamic w in shell.Windows())
                {
                    if ((long)w.HWND != root.ToInt64()) continue;
                    foreach (dynamic it in w.Document.Folder.Items())
                    {
                        string path = it.Path;
                        if (Match(path) || (IsImg(path) && string.Equals((string)it.Name, name, StringComparison.OrdinalIgnoreCase))) return path;
                    }
                }
            }
        }
        catch (Exception ex) { Log("HATA: " + ex.Message); }
        return null;
    }

    static void Trim() { GC.Collect(); SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1); }

    protected override void Dispose(bool d) { if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook); base.Dispose(d); }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32")] static extern IntPtr SetWindowsHookEx(int id, HookProc p, IntPtr mod, uint tid);
    [DllImport("user32")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    [DllImport("user32")] static extern short GetAsyncKeyState(int k);
    [DllImport("user32")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32")] static extern IntPtr GetAncestor(IntPtr h, int flags);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("kernel32")] static extern IntPtr GetModuleHandle(string? n);
    [DllImport("kernel32")] static extern bool SetProcessWorkingSetSize(IntPtr h, int min, int max);
    [DllImport("oleacc")] static extern int AccessibleObjectFromPoint(POINT pt, [MarshalAs(UnmanagedType.Interface)] out IAccessible acc, out object child);
}

// Çark penceresi — LIQUID GLASS sürümü.
// Pencere artık piksel başına alfa destekli (UpdateLayeredWindow): çark gerçekten yarı saydam camdır,
// arkasındaki Gezgin/masaüstü hafifçe görünür; yumuşak gölge ve ışık kenarlığı vardır.
// Tıklamayı geçirir, odak çalmaz. Fare konumu yalnızca açıkken 33 ms'de bir okunur.
class Wheel : Form
{
    const int S = 340, C = S / 2;                          // gölge için 20 px pay bırakıldı
    const float RIn = 54, ROut = 129, RHit = 138;          // dilim iç/dış yarıçapı, fare algılama sınırı
    const float RBody = 144;                               // cam gövde yarıçapı
    // Dilimler -120° (sol üst) ile başlayıp saat yönünde 60°'şer dizilir; ayırıcılar üstte/altta dikey.
    static readonly string[] L = { "PNG", "WEBP", "GIF", "PDF", "SVG", "JPG" };
    // MainForm ile aynı palet
    static readonly Color Gold = Color.FromArgb(111, 92, 255), Ice = Color.FromArgb(222, 217, 255),
        Txt = Color.FromArgb(244, 243, 252), Sub = Color.FromArgb(170, 170, 188), Tag = Color.FromArgb(166, 154, 255);

    // ---------- Yazı tipleri (SF Pro > Segoe UI Variable > Segoe UI > UiFont) ----------
    static readonly HashSet<string> Installed = LoadFonts();
    static HashSet<string> LoadFonts()
    {
        try
        {
            using var c = new InstalledFontCollection();
            return new HashSet<string>(c.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    static Font Glassy(float px, bool semi)
    {
        string[] cands = semi
            ? new[] { "SF Pro Display Semibold", "SF Pro Display", "Segoe UI Variable Display Semib", "Segoe UI Semibold" }
            : new[] { "SF Pro Text", "SF Pro Display", "Segoe UI Variable Text", "Segoe UI" };
        foreach (var n in cands)
            if (Installed.Contains(n))
                return new Font(n, px, semi && n == "SF Pro Display" ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        return semi ? UiFont.Semi(px) : UiFont.Regular(px);
    }

    readonly Font fLabel = Glassy(15f, true);
    readonly Font fTag = Glassy(9.5f, true);
    readonly Font fBig = Glassy(21f, true);
    readonly Font fName = Glassy(10f, false);

    readonly System.Windows.Forms.Timer _t = new() { Interval = 33 };
    string _path = ""; Action<string, string>? _cb; int _hot = -1;

    public Wheel()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
        StartPosition = FormStartPosition.Manual; Size = new Size(S, S);
        _t.Tick += (_, _) => Track();
    }
    protected override bool ShowWithoutActivation => true;
    // 0x80000 LAYERED | 0x08000000 NOACTIVATE | 0x80 TOOLWINDOW | 0x20 TRANSPARENT (tıklamayı geçirir)
    protected override CreateParams CreateParams { get { var c = base.CreateParams; c.ExStyle |= 0x80000 | 0x08000000 | 0x80 | 0x20; return c; } }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }   // çizim UpdateLayeredWindow ile yapılır

    public void Open(string path, Point p, Action<string, string> cb)
    {
        _path = path; _cb = cb; _hot = -1;
        var wa = Screen.FromPoint(p).WorkingArea;
        Location = new Point(Math.Clamp(p.X - S / 2, wa.Left - 20, wa.Right - S + 20), Math.Clamp(p.Y - S / 2, wa.Top - 20, wa.Bottom - S + 20));
        Show(); _t.Start(); Render();
    }

    public void Finish(bool cancel)
    {
        _t.Stop(); Hide();
        if (!cancel && _hot >= 0) _cb?.Invoke(_path, L[_hot]);
    }

    void Track()
    {
        var c = Cursor.Position; double dx = c.X - (Left + C), dy = c.Y - (Top + C), d = Math.Sqrt(dx * dx + dy * dy);
        int i = -1;
        if (d > RIn - 2 && d < RHit) i = (int)(((Math.Atan2(dy, dx) * 180 / Math.PI + 150) % 360 + 360) % 360 / 60);
        if (i != _hot) { _hot = i; Render(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _t.Dispose();
            fLabel.Dispose(); fTag.Dispose(); fBig.Dispose(); fName.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------- Piksel başına alfa ile gösterim ----------
    void Render()
    {
        if (!IsHandleCreated) return;
        using var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) Draw(g);

        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hbmp = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(mem, hbmp);
            var size = new WSize { W = S, H = S };
            var src = new WPoint();
            var bf = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref size, mem, ref src, 0, ref bf, 2);          // ULW_ALPHA
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    static Color Wh(float a) => Color.FromArgb(Math.Clamp((int)a, 0, 255), 255, 255, 255);
    static PointF Pt(double r, double deg) => new((float)(C + r * Math.Cos(deg * Math.PI / 180)), (float)(C + r * Math.Sin(deg * Math.PI / 180)));

    // Arka plandaki yumuşak ışık küresi (camın içinde görünen renk)
    static void Orb(Graphics g, float cx, float cy, float rx, float ry, Color c)
    {
        using var p = new GraphicsPath();
        p.AddEllipse(cx - rx, cy - ry, rx * 2, ry * 2);
        using var pg = new PathGradientBrush(p) { CenterColor = c, SurroundColors = new[] { Color.FromArgb(0, c) } };
        g.FillPath(pg, p);
    }

    // Cam disk: koyu şeffaf gövde + ışık küreleri + beyaz geçiş + üst yansıma + köşegen ışık kenarlığı
    static void GlassDisc(Graphics g, RectangleF r, int baseAlpha, float strength, float rimW)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(r);

        var st = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        using (var b = new SolidBrush(Color.FromArgb(baseAlpha, 12, 12, 20))) g.FillRectangle(b, r);
        Orb(g, r.X + r.Width * 0.72f, r.Y + r.Height * 0.25f, r.Width * 0.46f, r.Height * 0.40f, Color.FromArgb((int)(78 * strength), Gold));
        Orb(g, r.X + r.Width * 0.28f, r.Y + r.Height * 0.80f, r.Width * 0.42f, r.Height * 0.36f, Color.FromArgb((int)(52 * strength), 50, 110, 255));
        using (var body = new LinearGradientBrush(new RectangleF(r.X, r.Y, r.Width, r.Height + 1), Wh(30 * strength), Wh(6 * strength), 90f))
            g.FillRectangle(body, r);
        var top = new RectangleF(r.X, r.Y, r.Width, r.Height * 0.5f + 1);
        using (var sheen = new LinearGradientBrush(top, Wh(52 * strength), Wh(0), 90f))
            g.FillRectangle(sheen, top);
        g.Restore(st);

        using var rim = new LinearGradientBrush(new RectangleF(r.X, r.Y, r.Width, r.Height + 1), Wh(140), Wh(80), 45f);
        rim.InterpolationColors = new ColorBlend(3)
        {
            Colors = new[] { Wh(150 * strength), Wh(24 * strength), Wh(90 * strength) },
            Positions = new[] { 0f, 0.5f, 1f }
        };
        using var pen = new Pen(rim, rimW);
        g.DrawEllipse(pen, r);
    }

    void Draw(Graphics g)
    {
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // Yumuşak gölge
        for (int i = 16; i >= 0; i--)
        {
            float r = RBody + i * 0.9f;
            using var sb = new SolidBrush(Color.FromArgb(5, 0, 0, 0));
            g.FillEllipse(sb, C - r, C - r + 7, r * 2, r * 2);
        }

        // Cam gövde
        GlassDisc(g, new RectangleF(C - RBody, C - RBody, RBody * 2, RBody * 2), 150, 1f, 1.5f);

        // Kesikli halka + dört yönde küçük çentik
        using (var pen = new Pen(Wh(46), 1f) { DashPattern = new[] { 2f, 3f } }) g.DrawEllipse(pen, C - 137, C - 137, 274, 274);
        using (var pen = new Pen(Wh(70), 1.5f))
            for (int k = 0; k < 4; k++) g.DrawLine(pen, Pt(132, k * 90), Pt(141, k * 90));

        // Radyal ayırıcılar
        using (var pen = new Pen(Wh(48), 1.2f))
            for (int i = 0; i < 6; i++) g.DrawLine(pen, Pt(RIn, -150 + i * 60), Pt(ROut, -150 + i * 60));

        // Aktif dilim: mor cam dilim (parıltı + gövde + yansıma + ışık kenarı)
        if (_hot >= 0)
        {
            float a = -150 + _hot * 60;
            using var gp = new GraphicsPath();
            gp.AddArc(C - ROut, C - ROut, ROut * 2, ROut * 2, a, 60);
            gp.AddArc(C - RIn, C - RIn, RIn * 2, RIn * 2, a + 60, -60);
            gp.CloseFigure();

            using (var glow = new Pen(Color.FromArgb(50, Gold), 7f) { LineJoin = LineJoin.Round }) g.DrawPath(glow, gp);
            using (var b = new SolidBrush(Color.FromArgb(190, Gold))) g.FillPath(b, gp);

            var bnd = gp.GetBounds();
            var st = g.Save();
            g.SetClip(gp, CombineMode.Intersect);
            using (var gl = new LinearGradientBrush(new RectangleF(bnd.X, bnd.Y, bnd.Width, bnd.Height + 1), Wh(80), Wh(8), 90f))
                g.FillRectangle(gl, bnd);
            g.Restore(st);

            using (var pen = new Pen(Wh(150), 1.2f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, gp);
        }

        // Format etiketleri
        using (var sf = UiFont.Format(StringAlignment.Center, StringAlignment.Center))
            for (int i = 0; i < 6; i++)
            {
                var c = Pt((RIn + ROut) / 2, -120 + i * 60);
                using var tb = new SolidBrush(i == _hot ? Color.White : Txt);
                g.DrawString(L[i], fLabel, tb, new RectangleF(c.X - 40, c.Y - 12, 80, 24), sf);
            }

        // Orta kart: küçük cam disk
        float rc = RIn - 2;
        GlassDisc(g, new RectangleF(C - rc, C - rc, rc * 2, rc * 2), 175, 0.9f, 1.5f);

        // HEDEF / format / dosya adı
        using var sf2 = UiFont.Format(StringAlignment.Center, StringAlignment.Center);
        using (var b = new SolidBrush(Tag))
            g.DrawString(string.Join("\u2009", "HEDEF".ToCharArray()), fTag, b, new RectangleF(C - 40, C - 27, 80, 14), sf2);
        using (var b = new SolidBrush(_hot >= 0 ? Ice : Txt))
            g.DrawString(_hot >= 0 ? L[_hot] : "Seç", fBig, b, new RectangleF(C - 44, C - 15, 88, 28), sf2);

        string name = Path.GetFileName(_path);
        using var nf = UiFont.Format(StringAlignment.Near, StringAlignment.Center); nf.FormatFlags |= StringFormatFlags.NoWrap;
        float tw = Math.Min(g.MeasureString(name, fName, PointF.Empty, StringFormat.GenericTypographic).Width, 62), x0 = C - (tw + 14) / 2, ty = C + 22;
        using (var pen = new Pen(Sub, 1f)) { g.DrawRectangle(pen, x0, ty - 4, 9, 8); g.DrawLine(pen, x0 + 1, ty + 3, x0 + 4, ty); g.DrawLine(pen, x0 + 4, ty, x0 + 8, ty + 3); }
        using (var b = new SolidBrush(Sub))
        {
            g.FillEllipse(b, x0 + 6, ty - 3, 2, 2);
            g.DrawString(name, fName, b, new RectangleF(x0 + 13, ty - 7, tw + 2, 14), nf);
        }
    }

    // ---------- Win32 ----------
    [StructLayout(LayoutKind.Sequential)] struct WPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct WSize { public int W, H; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32", SetLastError = true)]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref WSize psize,
        IntPtr hdcSrc, ref WPoint pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32")] static extern bool DeleteObject(IntPtr o);
}