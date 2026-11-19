using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Mevcut Tool, Store ve Tray sınıflarıyla birlikte çalışır (Program.cs'e dokunmana gerek yok).
// Eski MainForm.cs dosyasının tamamını bununla değiştir.
//
// LIQUID GLASS sürümü (yerleşim, animasyonlar ve hit-test aynen korunur):
//  - Arka planda yumuşak renkli ışık küreleri; paneller bunların üzerinde yarı saydam cam gibi durur
//  - Her cam yüzey: şeffaf dolgu + üst yansıma (specular) + köşegen ışık kenarlığı + yumuşak gölge
//  - Anahtar: cam hap şeklinde kaydırıcı (iOS 26 tarzı)
//  - Yazı tipi: kuruluysa SF Pro, değilse Segoe UI Variable, o da yoksa eski UiFont
// Zamanlayıcı yalnızca animasyon sürerken çalışır; boştayken işlemci kullanımı sıfırdır.
//
// Toplu Dönüştür mantığı BatchConvert.cs içindedir; burada yalnızca sayfası çizilir.
class MainForm : Form
{
    const int W = 700, H = 430;
    const float UiScale = 1.20f; // Daha ferah pencere; mantıksal yerleşim korunur.

    static readonly Color Bg = Color.FromArgb(8, 8, 11);
    static readonly Color Card = Color.FromArgb(17, 17, 23);
    static readonly Color Line = Color.FromArgb(43, 43, 57);          // yalnızca Win11 DWM kenarlığı için
    static readonly Color Gold = Color.FromArgb(111, 92, 255);
    static readonly Color Ice = Color.FromArgb(222, 217, 255);
    static readonly Color Txt = Color.FromArgb(244, 243, 252);
    static readonly Color Sub = Color.FromArgb(170, 170, 188);
    static readonly Color Muted = Color.FromArgb(122, 122, 142);
    static readonly Color Hairline = Color.FromArgb(30, 255, 255, 255); // cam ayırıcı çizgiler

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

    // Piksel cinsinden font üretir (ölçekleme ScaleTransform ile yapılır).
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

    readonly List<Tool> _tools;
    readonly Image? _logo = LoadLogo();                 // marka alanındaki resim (yoksa "R" çizilir)
    readonly List<(Rectangle r, Action a)> _hits = new();
    readonly Font fTitle = Glassy(17f, true);
    readonly Font fSection = Glassy(10.5f, true);
    readonly Font fBody = Glassy(12.5f, false);
    readonly Font fSmall = Glassy(11f, false);
    readonly Font fIcon = Glassy(22f, true);

    int _sel;                                           // seçili uygulama
    float _repl, _replTarget;                           // "aynı dosyayı değiştir" seçeneği 0..1
    int _over = -1;
    float K => DeviceDpi / 96f * UiScale;
    int WL => Math.Max(1, (int)Math.Round(ClientSize.Width / K));    // gerçek mantıksal genişlik
    int HL => Math.Max(1, (int)Math.Round(ClientSize.Height / K));   // gerçek mantıksal yükseklik

    // --- Animasyon durumu ---
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    readonly Dictionary<string, bool> _on = new();      // hedef durum (kayıt defteri önbelleği)
    readonly Dictionary<string, float> _knob = new();   // anahtar ilerlemesi 0..1
    float _selY, _selTarget = 116;                      // kategori seçim çubuğu
    float _enterMs, _enterTotal = 280;                  // kartların giriş animasyonu
    float _a = 1f;                                      // çizim şeffaflığı (kart girişi için)
    long _last;
    static bool Fx => SystemInformation.UIEffectsEnabled;

    // Windows 11 (build 22000+): köşeleri, kenarlığı ve gölgeyi DWM çizer. Biz ayrıca Region/ana hat çizmeyiz.
    static readonly bool Win11 = Environment.OSVersion.Version.Build >= 22000;

    // Gezinme: -1 = "Rexael Tools > Ayarlar", 0.. = Uygulamalar
    int NavY(int i) => i < 0 ? 106 : _navY[i];
    int[] _navY = Array.Empty<int>();                   // her uygulamanın kenar çubuğundaki Y konumu
    readonly List<(string text, int y)> _heads = new(); // kategori başlıkları (Cat alanına göre)
    bool _editing, _fresh;                              // oto tıklayıcı: ms kutusu düzenleniyor mu
    string _buf = "";

    bool _auto;                                         // "Windows ile başlat"
    float _autoKnob;                                    // anahtar ilerlemesi 0..1

    public MainForm(List<Tool> tools)
    {
        _tools = tools;

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Rexael Tools";
        Icon = AppIcon.Get(false);
        BackColor = Bg;
        DoubleBuffered = true;
        KeyPreview = true;
        AllowDrop = true;
        ShowInTaskbar = false;

        ClientSize = new Size(W, H);
        KeyDown += OnKey;
        AutoClicker.Changed += OnClickerChanged;
        BatchConvert.Changed += OnClickerChanged;
        Updater.Changed += OnUpdaterChanged;

        foreach (var t in _tools)
        {
            bool on = Store.Get(t.Id, t.DefOn);
            _on[t.Id] = on;
            _knob[t.Id] = on ? 1f : 0f;
        }
        _auto = Autostart.Get();
        _autoKnob = _auto ? 1f : 0f;
        // Kenar çubuğu yerleşimi: araçlar Cat alanına göre gruplanır, her grubun başlığı olur.
        _navY = new int[_tools.Count];
        {
            int y = 156; string? cat = null;
            for (int i = 0; i < _tools.Count; i++)
            {
                if (_tools[i].Cat != cat)
                {
                    if (cat != null) y += 8;
                    cat = _tools[i].Cat;
                    _heads.Add((cat.ToUpperInvariant(), y));
                    y += 22;
                }
                _navY[i] = y;
                y += 39;
            }
        }
        _selY = _selTarget = NavY(_sel);
        _repl = _replTarget = Store.GetOpt(Store.ReplaceKey, false) ? 1f : 0f;
        _enterMs = 0;
        _enterTotal = 350;
        _anim.Tick += (_, _) => Tick();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var c = base.CreateParams;
            if (!Win11) c.ClassStyle |= 0x20000; // CS_DROPSHADOW (Win11'de DWM gölgesi var)
            return c;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        int scaledW = (int)Math.Round(W * K);
        int scaledH = (int)Math.Round(H * K);
        ClientSize = new Size(scaledW, scaledH);

        try
        {
            if (Win11)
            {
                // Köşe yuvarlama, kenarlık ve gölge tamamen Windows'a bırakılır (çift köşe olmaz).
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
                int border = Line.R | (Line.G << 8) | (Line.B << 16); // COLORREF
                DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
                var m = new MARGINS { L = 1, R = 1, T = 1, B = 1 };
                DwmExtendFrameIntoClientArea(Handle, ref m);
            }
        }
        catch { }

        ApplyWindowRegion();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyWindowRegion();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Pencere beklenen boyuttan farklı açıldıysa düzelt (alt tarafta boşluk kalmasın).
        if (WindowState == FormWindowState.Normal)
        {
            var want = new Size((int)Math.Round(W * DeviceDpi / 96f * UiScale), (int)Math.Round(H * DeviceDpi / 96f * UiScale));
            if (ClientSize != want) ClientSize = want;
        }
        Kick(); // açılış animasyonu
    }

    void ApplyWindowRegion()
    {
        if (!IsHandleCreated) return;

        // Win11'de ya da tam ekranda Region kullanılmaz; yalnızca Win10'da köşeleri biz yuvarlarız.
        if (Win11 || WindowState == FormWindowState.Maximized)
        {
            if (Region != null) Region = null;
            return;
        }

        int radius = Math.Max(1, (int)Math.Round(14 * K));
        using var shape = RoundRect(
            new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
            radius);
        Region = new Region(shape);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            AutoClicker.Changed -= OnClickerChanged;
            BatchConvert.Changed -= OnClickerChanged;
            Updater.Changed -= OnUpdaterChanged;
            _logo?.Dispose();
            _anim.Dispose();
            fTitle.Dispose();
            fSection.Dispose();
            fBody.Dispose();
            fSmall.Dispose();
            fIcon.Dispose();
        }
        base.Dispose(disposing);
    }

    // Logo: önce gömülü kaynak (Assets\logo.png), yoksa exe yanındaki Assets\logo.png.
    static Image? LoadLogo()
    {
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("logo.png");
            if (s != null) { using var tmp = new Bitmap(s); return new Bitmap(tmp); }   // akıştan bağımsız kopya

            var file = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.png");
            if (File.Exists(file)) { using var tmp = new Bitmap(file); return new Bitmap(tmp); }
        }
        catch (Exception ex) { Tray.Log("Logo yüklenemedi: " + ex.Message); }
        return null;
    }

    // ---------- Animasyon ----------
    static float C01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    static float Ease(float t) { t = C01(t); return 1f - (float)Math.Pow(1 - t, 3); }                       // easeOutCubic
    static float EaseIO(float t) { t = C01(t); return t < .5f ? 4 * t * t * t : 1f - (float)Math.Pow(-2 * t + 2, 3) / 2f; }
    static float Lf(float a, float b, float t) => a + (b - a) * t;
    static Color Lc(Color a, Color b, float t) =>
        Color.FromArgb((int)Lf(a.R, b.R, t), (int)Lf(a.G, b.G, t), (int)Lf(a.B, b.B, t));

    void Kick()
    {
        if (_anim.Enabled) return;
        _last = Environment.TickCount64;
        _anim.Start();
    }

    void Tick()
    {
        long now = Environment.TickCount64;
        float dt = Fx ? Math.Min(50f, now - _last) : 10000f;   // efektler kapalıysa anında bitir
        _last = now;
        bool busy = false;

        foreach (var t in _tools)
        {
            float target = _on[t.Id] ? 1f : 0f, k = _knob[t.Id];
            if (k != target)
            {
                k = target > k ? Math.Min(target, k + dt / 200f) : Math.Max(target, k - dt / 200f);
                _knob[t.Id] = k;
                if (k != target) busy = true;
            }
        }

        float at = _auto ? 1f : 0f;
        if (_autoKnob != at)
        {
            _autoKnob = at > _autoKnob ? Math.Min(at, _autoKnob + dt / 200f) : Math.Max(at, _autoKnob - dt / 200f);
            if (_autoKnob != at) busy = true;
        }

        if (_repl != _replTarget)
        {
            _repl = _replTarget > _repl ? Math.Min(_replTarget, _repl + dt / 200f) : Math.Max(_replTarget, _repl - dt / 200f);
            if (_repl != _replTarget) busy = true;
        }

        float d = _selTarget - _selY;
        if (Math.Abs(d) < 0.4f) _selY = _selTarget;
        else { _selY += d * (1f - (float)Math.Exp(-dt / 55f)); busy = true; }

        if (_enterMs < _enterTotal)
        {
            _enterMs = Math.Min(_enterTotal, _enterMs + dt);
            if (_enterMs < _enterTotal) busy = true;
        }

        Invalidate();
        if (!busy) _anim.Stop();
    }

    void SelectTool(int index)
    {
        CommitEdit();
        if (index == _sel) return;
        _sel = index;
        _selTarget = NavY(index);
        _enterMs = 0;
        _enterTotal = 350;
        Kick();
        Invalidate();
    }

    void ToggleAutostart()
    {
        bool on = !_auto;
        try { Autostart.Set(on); _auto = on; }
        catch (Exception ex) { Tray.Log("Başlangıç ayarı hatası: " + ex.Message); }
        Kick();
        Invalidate();
    }

    void SetReplace(bool v)
    {
        Store.SetOpt(Store.ReplaceKey, v);
        _replTarget = v ? 1f : 0f;
        Kick();
        Invalidate();
    }

    // ---------- Giriş / hit-test ----------
    Point Logical(Point p) => new(
        (int)Math.Round(p.X / K),
        (int)Math.Round(p.Y / K));

    int HitAt(Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].r.Contains(p)) return i;
        return -1;
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg == 0x84 && m.Result == (IntPtr)1)
        {
            long l = m.LParam.ToInt64();
            var screen = new Point(
                unchecked((short)(l & 0xFFFF)),
                unchecked((short)((l >> 16) & 0xFFFF)));
            var p = Logical(PointToClient(screen));

            if (p.Y < 70 && HitAt(p) < 0)
                m.Result = (IntPtr)2; // HTCAPTION
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int i = HitAt(Logical(e.Location));
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        if (i != _over)
        {
            _over = i;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_over != -1)
        {
            _over = -1;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        CommitEdit();

        int i = HitAt(Logical(e.Location));
        if (i >= 0) _hits[i].a();
    }

    // ---------- Çizim yardımcıları ----------
    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;

        if (radius <= 0)
        {
            p.AddRectangle(r);
            return p;
        }

        d = Math.Min(d, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    Color A(Color c) => _a >= 0.999f ? c : Color.FromArgb((int)(c.A * C01(_a)), c);

    static Color Wh(float a) => Color.FromArgb(Math.Clamp((int)a, 0, 255), 255, 255, 255);

    // Liquid Glass yüzey:
    //  gölge -> renk tonu -> cam gövde (üstten alta şeffaf beyaz) -> üst yansıma -> köşegen ışık kenarlığı
    void Glass(Graphics g, Rectangle r, int radius, Color? tint = null, float tintAmt = 0f,
               float strength = 1f, bool shadow = false)
    {
        using var path = RoundRect(r, radius);

        if (shadow)
        {
            for (int i = 4; i >= 1; i--)
            {
                using var sp = RoundRect(new Rectangle(r.X - i, r.Y - i + 4, r.Width + i * 2, r.Height + i * 2), radius + i);
                using var sb = new SolidBrush(A(Color.FromArgb(10, 0, 0, 0)));
                g.FillPath(sb, sp);
            }
        }

        if (tint.HasValue && tintAmt > 0f)
        {
            using var tb = new SolidBrush(A(Color.FromArgb(Math.Min(255, (int)(60 * tintAmt)), tint.Value)));
            g.FillPath(tb, path);
        }

        var rf = new RectangleF(r.X, r.Y, r.Width, r.Height + 1);
        using (var body = new LinearGradientBrush(rf, A(Wh(34 * strength)), A(Wh(9 * strength)), 90f))
            g.FillPath(body, path);

        // Üst yansıma (specular): yüzeyin üst yarısında beyazdan şeffafa
        var st = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        var top = new RectangleF(r.X, r.Y, r.Width, Math.Max(2f, r.Height * 0.5f) + 1);
        using (var sheen = new LinearGradientBrush(top, A(Wh(46 * strength)), A(Wh(0)), 90f))
            g.FillRectangle(sheen, top);
        g.Restore(st);

        // Işık kenarlığı: sol-üst ve sağ-alt köşede parlak, ortada sönük
        using var rim = new LinearGradientBrush(rf, A(Wh(130 * strength)), A(Wh(75 * strength)), 45f);
        rim.InterpolationColors = new ColorBlend(3)
        {
            Colors = new[] { A(Wh(130 * strength)), A(Wh(22 * strength)), A(Wh(75 * strength)) },
            Positions = new[] { 0f, 0.5f, 1f }
        };
        using var pen = new Pen(rim, 1f);
        g.DrawPath(pen, path);
    }

    // Arka plandaki yumuşak ışık küresi (camın "arkasında" görünen renk)
    static void Orb(Graphics g, float cx, float cy, float rx, float ry, Color c)
    {
        using var p = new GraphicsPath();
        p.AddEllipse(cx - rx, cy - ry, rx * 2, ry * 2);
        using var pg = new PathGradientBrush(p)
        {
            CenterColor = c,
            SurroundColors = new[] { Color.FromArgb(0, c) }
        };
        g.FillPath(pg, p);
    }

    // Küçük camsı küre (pencere düğmeleri)
    void GlassDot(Graphics g, Rectangle r, Color c)
    {
        using (var b = new SolidBrush(c)) g.FillEllipse(b, r);
        var hl = new RectangleF(r.X + 2.2f, r.Y + 0.8f, r.Width - 4.4f, r.Height * 0.5f);
        using (var hb = new LinearGradientBrush(hl, Wh(150), Wh(0), 90f))
            g.FillEllipse(hb, hl);
        using var pen = new Pen(Wh(70), 1f);
        g.DrawEllipse(pen, r);
    }

    void DrawText(
        Graphics g,
        string value,
        Font font,
        Color color,
        Rectangle r,
        StringAlignment horizontal = StringAlignment.Near,
        StringAlignment vertical = StringAlignment.Near)
    {
        // Geometri K ölçeğinde çizilmeye devam eder; metni ise doğrudan
        // ekran piksel koordinatlarında çizerek 1.20x dönüşümün yazıları
        // yumuşatmasını önleriz. Animasyonların ötelemesi de korunur.
        using var transform = g.Transform;
        PointF[] corners =
        {
            new PointF(r.Left, r.Top),
            new PointF(r.Right, r.Bottom)
        };
        transform.TransformPoints(corners);

        var pixelRect = Rectangle.FromLTRB(
            (int)Math.Round(corners[0].X),
            (int)Math.Round(corners[0].Y),
            (int)Math.Round(corners[1].X),
            (int)Math.Round(corners[1].Y));

        var state = g.Save();
        try
        {
            g.ResetTransform();
            Color drawColor = A(color);
            g.TextRenderingHint = drawColor.A == 255
                ? System.Drawing.Text.TextRenderingHint.ClearTypeGridFit
                : System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using var pixelFont = new Font(
                font.FontFamily,
                font.Size * K,
                font.Style,
                GraphicsUnit.Pixel);
            using var brush = new SolidBrush(drawColor);
            using var format = UiFont.Format(horizontal, vertical);
            g.DrawString(value, pixelFont, brush, pixelRect, format);
        }
        finally
        {
            g.Restore(state);
        }
    }

    bool Hot(Rectangle r) =>
        r.Contains(Logical(PointToClient(Cursor.Position)));

    void DrawOption(Graphics g, Rectangle r, string title, string sub, float sel)
    {
        Glass(g, r, 11, Gold, 1.3f * sel, Lf(0.7f, 1.1f, sel));

        var rc = new Rectangle(r.X + 14, r.Y + 17, 18, 18);
        using (var pen = new Pen(A(Lc(Muted, Ice, sel)), 1.5f))
            g.DrawEllipse(pen, rc);

        float d = 10f * sel;
        using (var b = new SolidBrush(A(Ice)))
            g.FillEllipse(b, rc.X + 9 - d / 2f, rc.Y + 9 - d / 2f, d, d);

        DrawText(g, title, fBody, Txt, new Rectangle(r.X + 44, r.Y + 8, r.Width - 56, 20));
        DrawText(g, sub, fSmall, Sub, new Rectangle(r.X + 44, r.Y + 28, r.Width - 56, 20));
    }

    // Cam hap anahtar: renkli cam yol + beyaz cam kaydırıcı
    void DrawSwitch(Graphics g, Rectangle sw, float k)
    {
        Color baseC = Lc(Color.FromArgb(120, 120, 140), Gold, k);
        Glass(g, sw, 12, baseC, 1.5f + 1.1f * k, 0.9f);

        int kw = 24, kh = sw.Height - 4;
        var knob = new Rectangle((int)Math.Round(Lf(sw.X + 2, sw.Right - 2 - kw, k)), sw.Y + 2, kw, kh);
        Glass(g, knob, kh / 2, Color.White, 3.6f, 1.2f, true);
    }

    void Toggle(Tool tool)
    {
        bool on = !_on[tool.Id];
        _on[tool.Id] = on;
        Store.Set(tool.Id, on);
        Kick();
        Invalidate();

        // Aracı animasyonun ilk karesi çizildikten sonra aç/kapat (takılma olmasın).
        BeginInvoke(new Action(() =>
        {
            try { tool.Apply(on); }
            catch (Exception ex)
            {
                Tray.Log("Araç hatası: " + ex.Message);
                _on[tool.Id] = !on;
                Store.Set(tool.Id, !on);
                Kick();
            }
        }));
    }

    // ---------- AutoClicker ayarları ----------
    // (BatchConvert.Changed olayı da bu metodu kullanır: yalnızca yeniden çizdirir.)
    void OnClickerChanged() { if (!IsDisposed && IsHandleCreated) Invalidate(); }

    // Güncelleyici arka plan iş parçacığından haber verir; çizimi arayüz iş parçacığında yenile.
    void OnUpdaterChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => { if (!IsDisposed) { Kick(); Invalidate(); } })); } catch { }
    }

    static int Step(int v) => v >= 1000 ? 100 : v >= 100 ? 10 : v >= 20 ? 5 : 1;
    static int DigitOf(Keys k) =>
        k >= Keys.D0 && k <= Keys.D9 ? k - Keys.D0 :
        k >= Keys.NumPad0 && k <= Keys.NumPad9 ? k - Keys.NumPad0 : -1;

    void StartEdit() { _editing = true; _fresh = true; _buf = AutoClicker.Interval.ToString(); Invalidate(); }

    void CommitEdit()
    {
        if (!_editing) return;
        _editing = false;
        if (int.TryParse(_buf, out int v) && v > 0) AutoClicker.Interval = v;
        Invalidate();
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (_editing)
        {
            e.Handled = e.SuppressKeyPress = true;
            if (e.KeyCode == Keys.Enter) CommitEdit();
            else if (e.KeyCode == Keys.Escape) { _editing = false; Invalidate(); }
            else if (e.KeyCode == Keys.Back)
            {
                if (_fresh) { _buf = ""; _fresh = false; }
                else if (_buf.Length > 0) _buf = _buf[..^1];
                Invalidate();
            }
            else
            {
                int d = DigitOf(e.KeyCode);
                if (d < 0) return;
                if (_fresh) { _buf = ""; _fresh = false; }
                if (_buf.Length < 5) _buf += d;
                Invalidate();
            }
            return;
        }
        if (e.KeyCode == Keys.Escape) Close();
    }

    void Chip(Graphics g, Rectangle r, string text, bool sel)
    {
        Glass(g, r, 9, Gold, sel ? 1.3f : 0f, sel ? 1.1f : Hot(r) ? 1.5f : 0.7f);
        DrawText(g, text, fSmall, sel ? Ice : Sub, r, StringAlignment.Center, StringAlignment.Center);
    }

    void DrawClicker(Graphics g)
    {
        float e1 = Ease((_enterMs - 70f) / 280f);
        var state = g.Save();
        g.TranslateTransform((1f - e1) * 26f, 0);
        _a = e1;

        DrawText(g, "AYARLAR", fSection, Muted, new Rectangle(180, 212, 200, 16));
        DrawText(g, AutoClicker.Status, fSmall, AutoClicker.Running ? Ice : Muted,
            new Rectangle(384, 212, 300, 16), StringAlignment.Far);
        Glass(g, new Rectangle(176, 236, 508, 126), 14, null, 0f, 1f, true);

        // Aralık: [−] [ 100 ms ] [+]  (kutuya tıklayıp doğrudan yazılabilir)
        DrawText(g, "Tıklama aralığı", fBody, Txt, new Rectangle(192, 244, 300, 20));
        DrawText(g, "İki tıklama arasında beklenecek süre (milisaniye)", fSmall, Sub, new Rectangle(192, 264, 310, 16));

        int ms = AutoClicker.Interval;
        var minus = new Rectangle(514, 246, 30, 30);
        var box = new Rectangle(548, 246, 86, 30);
        var plus = new Rectangle(638, 246, 30, 30);
        Glass(g, minus, 9, null, 0f, Hot(minus) ? 1.5f : 0.8f);
        Glass(g, plus, 9, null, 0f, Hot(plus) ? 1.5f : 0.8f);
        DrawText(g, "−", fBody, Txt, minus, StringAlignment.Center, StringAlignment.Center);
        DrawText(g, "+", fBody, Txt, plus, StringAlignment.Center, StringAlignment.Center);
        Glass(g, box, 9, Gold, _editing ? 1.1f : 0f, _editing ? 1.2f : Hot(box) ? 1.2f : 0.5f);
        DrawText(g, _editing ? _buf + "|" : ms + " ms", fBody, _editing ? Ice : Txt, box,
            StringAlignment.Center, StringAlignment.Center);

        _hits.Add((minus, () => { _editing = false; AutoClicker.Interval = AutoClicker.Interval - Step(AutoClicker.Interval); }));
        _hits.Add((plus, () => { _editing = false; AutoClicker.Interval = AutoClicker.Interval + Step(AutoClicker.Interval); }));
        _hits.Add((box, StartEdit));

        // Hızlı seçim
        int[] presets = { 10, 50, 100, 250, 500, 1000 };
        for (int i = 0; i < presets.Length; i++)
        {
            int v = presets[i];
            var r = new Rectangle(192 + i * 80, 288, 72, 26);
            Chip(g, r, v + " ms", ms == v && !_editing);
            _hits.Add((r, () => { _editing = false; AutoClicker.Interval = v; }));
        }

        // Fare düğmesi + kısayol
        DrawText(g, "Düğme", fSmall, Sub, new Rectangle(192, 326, 48, 26), vertical: StringAlignment.Center);
        for (int i = 0; i < 3; i++)
        {
            int b = i;
            var r = new Rectangle(240 + i * 58, 326, 52, 26);
            Chip(g, r, AutoClicker.Buttons[i], AutoClicker.Button == i);
            _hits.Add((r, () => AutoClicker.Button = b));
        }
        DrawText(g, "Kısayol", fSmall, Sub, new Rectangle(432, 326, 52, 26), vertical: StringAlignment.Center);
        for (int i = 0; i < 4; i++)
        {
            int k = i;
            var r = new Rectangle(486 + i * 48, 326, 42, 26);
            Chip(g, r, "F" + (6 + i), AutoClicker.Hotkey == i);
            _hits.Add((r, () => AutoClicker.Hotkey = k));
        }

        g.Restore(state);
        _a = 1f;
    }

    // ---------- Toplu dönüştürme (mantık: BatchConvert.cs) ----------
    bool _dragOver;
    int BatchIndex => _tools.FindIndex(t => t.Id == BatchConvert.Id);

    // Dosyaları pencerenin herhangi bir yerine sürükleyip bırakmak yeterli.
    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (BatchIndex >= 0 && !BatchConvert.Running && e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
            _dragOver = true;
            if (_sel != BatchIndex) SelectTool(BatchIndex);
            Invalidate();
        }
        else e.Effect = DragDropEffects.None;
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        if (_dragOver) { _dragOver = false; Invalidate(); }
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        _dragOver = false;
        if (BatchIndex >= 0 && e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
        {
            if (_sel != BatchIndex) SelectTool(BatchIndex);
            BatchConvert.AddFiles(paths);
        }
        Invalidate();
    }

    // Toplu Dönüştür sayfası: açma/kapama kartı yok; tüm alan dosya ekleme ve ayarlara ayrıldı.
    void DrawBatch(Graphics g)
    {
        float e0 = Ease(_enterMs / 280f);
        var state = g.Save();
        g.TranslateTransform((1f - e0) * 26f, 0);
        _a = e0;

        bool run = BatchConvert.Running;
        var files = BatchConvert.Files;

        DrawText(g, "TOPLU DÖNÜŞTÜR", fSection, Muted, new Rectangle(180, 91, 200, 16));
        if (BatchConvert.Status.Length > 0)
            DrawText(g, BatchConvert.Status, fSmall, run ? Ice : Muted,
                new Rectangle(384, 91, 296, 16), StringAlignment.Far);

        // Büyük bırakma alanı
        var dz = new Rectangle(176, 116, 508, 112);
        Glass(g, dz, 14, Gold, _dragOver ? 1.4f : 0f, _dragOver ? 1.3f : 0.7f, true);
        using (var dp = RoundRect(new Rectangle(dz.X + 6, dz.Y + 6, dz.Width - 12, dz.Height - 12), 10))
        using (var dpen = new Pen(A(Wh(_dragOver ? 140 : 55)), 1f) { DashPattern = new[] { 3f, 3f } })
            g.DrawPath(dpen, dp);

        string t1, t2;
        if (files.Count == 0)
        {
            t1 = "Fotoğrafları buraya sürükle";
            t2 = "PNG, JPG, WEBP, GIF, BMP • klasör de bırakabilirsin";
        }
        else
        {
            t1 = files.Count + " dosya seçili";
            t2 = string.Join(", ", files.Take(2).Select(p => Path.GetFileName(p)))
                 + (files.Count > 2 ? " +" + (files.Count - 2) : "");
            if (t2.Length > 60) t2 = t2.Substring(0, 59) + "…";
        }
        DrawText(g, t1, fBody, Txt, new Rectangle(dz.X, dz.Y + 14, dz.Width, 22), StringAlignment.Center);
        DrawText(g, t2, fSmall, Sub, new Rectangle(dz.X, dz.Y + 38, dz.Width, 18), StringAlignment.Center);

        var add = new Rectangle(334, dz.Y + 66, 100, 30);
        var clr = new Rectangle(442, dz.Y + 66, 84, 30);
        Glass(g, add, 9, null, 0f, !run && Hot(add) ? 1.5f : 0.8f);
        Glass(g, clr, 9, null, 0f, !run && files.Count > 0 && Hot(clr) ? 1.5f : 0.8f);
        DrawText(g, "Dosya ekle", fSmall, run ? Muted : Txt, add, StringAlignment.Center, StringAlignment.Center);
        DrawText(g, "Temizle", fSmall, !run && files.Count > 0 ? Txt : Muted, clr,
            StringAlignment.Center, StringAlignment.Center);
        _hits.Add((dz, () => BatchConvert.AddDialog(this)));   // alanın kendisine tıklayınca da dosya seçilir
        _hits.Add((add, () => BatchConvert.AddDialog(this)));
        _hits.Add((clr, BatchConvert.Clear));

        // Seçenekler kartı
        Glass(g, new Rectangle(176, 240, 508, 122), 14, null, 0f, 1f, true);

        DrawText(g, "Hedef", fSmall, Sub, new Rectangle(192, 258, 44, 26), vertical: StringAlignment.Center);
        for (int i = 0; i < BatchConvert.Formats.Length; i++)
        {
            int k = i;
            var r = new Rectangle(240 + i * 70, 258, 64, 26);
            Chip(g, r, BatchConvert.Formats[i], BatchConvert.Format == i);
            _hits.Add((r, () => { if (!BatchConvert.Running) BatchConvert.Format = k; Invalidate(); }));
        }

        var zipR = new Rectangle(192, 312, 62, 26);
        Chip(g, zipR, "ZIP’le", BatchConvert.Zip);
        _hits.Add((zipR, () => { if (!BatchConvert.Running) BatchConvert.Zip = !BatchConvert.Zip; Invalidate(); }));

        var pb = new Rectangle(266, 320, 272, 10);
        Glass(g, pb, 5, null, 0f, 0.6f);
        float ratio = BatchConvert.Total > 0 ? C01(BatchConvert.Done / (float)BatchConvert.Total) : 0f;
        if (ratio > 0f)
            Glass(g, new Rectangle(pb.X, pb.Y, Math.Max(10, (int)(pb.Width * ratio)), pb.Height), 5, Gold, 2.4f, 1f);

        var go = new Rectangle(552, 310, 114, 30);
        bool can = files.Count > 0 && !run;
        Glass(g, go, 11, Gold, can ? (Hot(go) ? 3.8f : 3.1f) : 0.4f, can ? 1.2f : 0.6f, can);
        DrawText(g, run ? "Çalışıyor…" : "Dönüştür", fBody, can ? Txt : Muted, go,
            StringAlignment.Center, StringAlignment.Center);
        _hits.Add((go, BatchConvert.Run));

        g.Restore(state);
        _a = 1f;
    }

    // "Rexael Tools > Ayarlar" sayfası.
    void DrawGeneral(Graphics g)
    {
        float k = EaseIO(_autoKnob);

        DrawText(g, "AYARLAR", fSection, Muted, new Rectangle(180, 91, 250, 16));

        float e0 = Ease(_enterMs / 280f);
        var card = new Rectangle(176, 116, 508, 82);

        var state = g.Save();
        g.TranslateTransform((1f - e0) * 26f, 0);
        _a = e0;

        Glass(g, card, 14, null, 0f, 1f, true);

        var icon = new Rectangle(card.X + 14, card.Y + 17, 48, 48);
        Glass(g, icon, 13, Gold, 1.5f, 1.1f);
        DrawText(g, "R", fIcon, Ice, icon, StringAlignment.Center, StringAlignment.Center);

        DrawText(g, "Windows ile başlat", fBody, Txt,
            new Rectangle(card.X + 76, card.Y + 15, 300, 20));
        DrawText(g, "Bilgisayar açıldığında Rexael Tools sistem tepsisinde kendiliğinden çalışır.", fSmall, Sub,
            new Rectangle(card.X + 76, card.Y + 37, 336, 36));

        var sw = new Rectangle(card.Right - 62, card.Y + 19, 42, 23);
        DrawSwitch(g, sw, k);
        DrawText(g, _auto ? "Açık" : "Kapalı", fSmall, Lc(Muted, Ice, k),
            new Rectangle(card.Right - 74, card.Y + 47, 66, 16),
            StringAlignment.Center, StringAlignment.Center);

        g.Restore(state);
        _a = 1f;

        _hits.Add((new Rectangle(card.Right - 84, card.Y + 8, 76, 66), ToggleAutostart));

        // ---- Güncelleme kartı ----
        float e1 = Ease((_enterMs - 70f) / 280f);
        var upd = new Rectangle(176, card.Bottom + 12, 508, 82);
        var st2 = g.Save();
        g.TranslateTransform((1f - e1) * 26f, 0);
        _a = e1;

        Glass(g, upd, 14, null, 0f, 1f, true);
        var uicon = new Rectangle(upd.X + 14, upd.Y + 17, 48, 48);
        Glass(g, uicon, 13, Gold, 1.5f, 1.1f);
        DrawText(g, "G", fIcon, Ice, uicon, StringAlignment.Center, StringAlignment.Center);

        DrawText(g, "Güncellemeleri denetle", fBody, Txt, new Rectangle(upd.X + 76, upd.Y + 15, 300, 20));
        string info = Updater.Status.Length > 0
            ? $"Kurulu sürüm {Updater.Current} · {Updater.Status}"
            : $"Kurulu sürüm {Updater.Current}. Ayrıntılar %TEMP%\\FormatCarki.log dosyasında.";
        DrawText(g, info, fSmall, Sub, new Rectangle(upd.X + 76, upd.Y + 37, 320, 36));

        var btn = new Rectangle(upd.Right - 106, upd.Y + 25, 92, 32);
        Chip(g, btn, Updater.Busy ? "Bakılıyor…" : "Denetle", false);

        g.Restore(st2);
        _a = 1f;

        _hits.Add((btn, () => { if (!Updater.Busy) _ = Updater.CheckAsync(true); }));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;
        g.Clear(Bg);
        g.ScaleTransform(K, K);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Gri tonlamalı kenar yumuşatma: ölçek, saydamlık ve animasyonla tutarlı.
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        _hits.Clear();
        _a = 1f;

        // Camın arkasındaki ışık küreleri: şeffaf yüzeyler bunlardan renk alır.
        Orb(g, 90, 360, 260, 200, Color.FromArgb(70, 50, 110, 255));
        Orb(g, 620, 60, 280, 200, Color.FromArgb(95, 111, 92, 255));
        Orb(g, 430, 400, 240, 150, Color.FromArgb(48, 190, 80, 230));

        // Cam katmanlar: başlık, kenar çubuğu, alt çubuk
        using (var hb = new LinearGradientBrush(new Rectangle(0, 0, WL, 71), Wh(16), Wh(5), 90f))
            g.FillRectangle(hb, 0, 0, WL, 70);
        using (var sb = new SolidBrush(Wh(8)))
        {
            g.FillRectangle(sb, 0, 70, 158, HL - 126);
            g.FillRectangle(sb, 0, HL - 56, WL, 56);
        }

        using (var pen = new Pen(Hairline))
        {
            if (!Win11)
            {
                using var outline = RoundRect(
                    new Rectangle(0, 0, WL - 1, HL - 1), 14);
                g.DrawPath(pen, outline);
            }
            g.DrawLine(pen, 0, 70, WL, 70);
            g.DrawLine(pen, 0, HL - 56, WL, HL - 56);
            g.DrawLine(pen, 158, 70, 158, HL - 56);
        }

        // macOS pencere kontrolleri (camsı küreler).
        var close = new Rectangle(18, 19, 12, 12);
        var minimize = new Rectangle(38, 19, 12, 12);
        var zoom = new Rectangle(58, 19, 12, 12);

        GlassDot(g, close, Color.FromArgb(239, 91, 82));
        GlassDot(g, minimize, Color.FromArgb(235, 190, 70));
        GlassDot(g, zoom, Color.FromArgb(87, 181, 96));

        _hits.Add((close, Close));
        _hits.Add((minimize, () => WindowState = FormWindowState.Minimized));
        _hits.Add((zoom, () =>
        {
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
                ClientSize = new Size(
                    (int)Math.Round(W * DeviceDpi / 96f * UiScale),
                    (int)Math.Round(H * DeviceDpi / 96f * UiScale));
                ApplyWindowRegion();
            }
            else
            {
                WindowState = FormWindowState.Maximized;
            }
        }
        ));

        // Marka alanı.
        var logoRect = new Rectangle(91, 14, 40, 40);       // 40x40 mantıksal piksel
        if (_logo != null)
        {
            var oldMode = g.InterpolationMode;
            var oldOffset = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_logo, logoRect);
            g.InterpolationMode = oldMode;
            g.PixelOffsetMode = oldOffset;
        }
        else
        {
            Glass(g, logoRect, 20, Gold, 3.2f, 1.2f, true);
            DrawText(g, "R", fTitle, Txt, logoRect,
                StringAlignment.Center, StringAlignment.Center);
        }
        DrawText(g, "Rexael sadsadTools", fTitle, Txt,
            new Rectangle(145, 13, 300, 24));
        DrawText(g, "Your tools. One workspace.", fSmall, Sub,
            new Rectangle(146, 37, 320, 17));

        // Kategoriler: cam seçim hapı _selY konumunda kayar.
        DrawText(g, "REXAEL TOOLS", fSection, Muted,
            new Rectangle(20, 84, 120, 16));
        foreach (var h in _heads)
            DrawText(g, h.text, fSection, Muted, new Rectangle(20, h.y, 120, 16));

        int selY = (int)Math.Round(_selY);
        Glass(g, new Rectangle(12, selY, 134, 32), 10, Gold, 1.4f, 1.1f);

        {
            var r = new Rectangle(12, NavY(-1), 134, 32);
            float near = C01(1f - Math.Abs(r.Y - _selY) / 39f);

            if (near < 0.5f && Hot(r))
                Glass(g, r, 10, null, 0f, 0.55f);

            DrawText(g, "Ayarlar", fBody, Lc(Txt, Ice, near),
                new Rectangle(r.X + 12, r.Y, 100, r.Height),
                vertical: StringAlignment.Center);

            _hits.Add((r, () => SelectTool(-1)));
        }

        for (int i = 0; i < _tools.Count; i++)
        {
            var t = _tools[i];
            int index = i;
            var r = new Rectangle(12, NavY(i), 134, 32);
            float near = C01(1f - Math.Abs(r.Y - _selY) / 39f);

            if (near < 0.5f && Hot(r))
                Glass(g, r, 10, null, 0f, 0.55f);

            DrawText(g, t.Name, fBody, Lc(Txt, Ice, near),
                new Rectangle(r.X + 12, r.Y, 100, r.Height),
                vertical: StringAlignment.Center);

            // Açık/kapalı göstergesi (Toplu Dönüştür her zaman açık olduğu için gösterilmez)
            if (t.Id != BatchConvert.Id)
                using (var dot = new SolidBrush(_on[t.Id] ? Ice : Muted))
                    g.FillEllipse(dot, r.Right - 18, r.Y + 13, 6, 6);

            _hits.Add((r, () => SelectTool(index)));
        }

        // İçerik alanı: seçili uygulamanın açma/kapama anahtarı ve ayarları.
        if (_sel < 0)
            DrawGeneral(g);
        else if (_tools.Count > 0 && _tools[_sel].Id == BatchConvert.Id)
            DrawBatch(g);                                   // açma/kapama kartı olmadan, tam sayfa
        else if (_tools.Count > 0)
        {
            var tool = _tools[_sel];
            bool on = _on[tool.Id];
            float k = EaseIO(_knob[tool.Id]);

            DrawText(g, "UYGULAMA", fSection, Muted,
                new Rectangle(180, 91, 250, 16));

            float e0 = Ease(_enterMs / 280f);
            var card = new Rectangle(176, 116, 508, 82);

            var state = g.Save();
            g.TranslateTransform((1f - e0) * 26f, 0);
            _a = e0;

            Glass(g, card, 14, null, 0f, 1f, true);

            var icon = new Rectangle(card.X + 14, card.Y + 17, 48, 48);
            Glass(g, icon, 13, Gold, 1.5f, 1.1f);
            DrawText(g, string.IsNullOrWhiteSpace(tool.Name) ? "•" : tool.Name.Substring(0, 1).ToUpperInvariant(),
                fIcon, Ice, icon, StringAlignment.Center, StringAlignment.Center);

            DrawText(g, tool.Name, fBody, Txt,
                new Rectangle(card.X + 76, card.Y + 15, 300, 20));
            DrawText(g, tool.Desc, fSmall, Sub,
                new Rectangle(card.X + 76, card.Y + 37, 336, 36));

            var sw = new Rectangle(card.Right - 62, card.Y + 19, 42, 23);
            DrawSwitch(g, sw, k);
            DrawText(g, on ? "Açık" : "Kapalı", fSmall, Lc(Muted, Ice, k),
                new Rectangle(card.Right - 74, card.Y + 47, 66, 16),
                StringAlignment.Center, StringAlignment.Center);

            g.Restore(state);
            _a = 1f;

            // Yalnızca anahtar bölgesi tıklanınca aç/kapa (kartın tamamı değil).
            var captured = tool;
            _hits.Add((new Rectangle(card.Right - 84, card.Y + 8, 76, 66), () => Toggle(captured)));

            // Ayarlar
            if (tool.Id == "format-carki")
            {
                float e1 = Ease((_enterMs - 70f) / 280f);
                state = g.Save();
                g.TranslateTransform((1f - e1) * 26f, 0);
                _a = e1;

                DrawText(g, "AYARLAR", fSection, Muted, new Rectangle(180, 212, 250, 16));
                Glass(g, new Rectangle(176, 236, 508, 126), 14, null, 0f, 1f, true);

                var o1 = new Rectangle(184, 244, 492, 52);
                var o2 = new Rectangle(184, 302, 492, 52);
                DrawOption(g, o1, "Yeni dosya oluştur",
                    "Orijinal dosya kalır; dönüştürülen kopya yanına eklenir.", 1f - _repl);
                DrawOption(g, o2, "Aynı dosyayı değiştir",
                    "Yeni formatlı dosya oluşur, orijinal Geri Dönüşüm Kutusu’na taşınır.", _repl);

                g.Restore(state);
                _a = 1f;

                _hits.Add((o1, () => SetReplace(false)));
                _hits.Add((o2, () => SetReplace(true)));
            }
            else if (tool.Id == AutoClicker.Id)
                DrawClicker(g);
        }

        // Alt bilgi ve Tamam düğmesi.
        // Bilgi simgesi font glyph'i yerine çizilir (her yazı tipinde aynı görünür).
        float iy = HL - 28;
        using (var pen = new Pen(Muted, 1.2f))
            g.DrawEllipse(pen, 19f, iy - 6.5f, 13f, 13f);
        using (var mb = new SolidBrush(Muted))
        {
            g.FillEllipse(mb, 24.7f, iy - 3.7f, 1.7f, 1.7f);
            g.FillRectangle(mb, 24.7f, iy - 0.8f, 1.7f, 4.4f);
        }
        DrawText(g, "Kapalı uygulamalar bellek ve işlemci kullanmaz.",
            fSmall, Muted,
            new Rectangle(40, HL - 56, 420, 56),
            vertical: StringAlignment.Center);

        var ok = new Rectangle(WL - 112 - 16, HL - 43, 112, 30);
        Glass(g, ok, 11, Gold, Hot(ok) ? 3.8f : 3.1f, 1.2f, true);

        DrawText(g, "Tamam", fBody, Txt, ok,
            StringAlignment.Center, StringAlignment.Center);

        _hits.Add((ok, Close));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS { public int L, R, T, B; }

    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int valueSize);
}