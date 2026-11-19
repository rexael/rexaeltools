using System.Diagnostics;
using System.Runtime.InteropServices;

// Oto tıklayıcı: kısayolla (F6–F9) başlar/durur; imlecin bulunduğu yerde N ms'de bir tıklar.
// Araç kapalıyken kısayol kaydı da iş parçacığı da yoktur → işlemci/bellek kullanımı sıfır.
static class AutoClicker
{
    public const string Id = "autoclicker";
    const string KInterval = "autoclicker.interval", KButton = "autoclicker.button", KHotkey = "autoclicker.hotkey";
    static readonly string[] Keys = { "F6", "F7", "F8", "F9" };
    public static readonly string[] Buttons = { "Sol", "Sağ", "Orta" };

    static volatile int _ms = Math.Clamp(Store.GetInt(KInterval, 100), 1, 60000);
    static volatile int _btn = Math.Clamp(Store.GetInt(KButton, 0), 0, 2);
    static int _key = Math.Clamp(Store.GetInt(KHotkey, 0), 0, 3);

    static CancellationTokenSource? _cts;
    static HotWin? _win;

    public static event Action? Changed;
    public static bool Enabled { get; private set; }
    public static bool HotkeyOk { get; private set; } = true;
    public static bool Running => _cts != null;

    // Aralık çalışırken de değiştirilebilir; döngü her turda güncel değeri okur.
    public static int Interval
    {
        get => _ms;
        set { _ms = Math.Clamp(value, 1, 60000); Store.SetInt(KInterval, _ms); Changed?.Invoke(); }
    }
    public static int Button
    {
        get => _btn;
        set { _btn = Math.Clamp(value, 0, 2); Store.SetInt(KButton, _btn); Changed?.Invoke(); }
    }
    public static int Hotkey
    {
        get => _key;
        set { _key = Math.Clamp(value, 0, 3); Store.SetInt(KHotkey, _key); if (Enabled) Register(); Changed?.Invoke(); }
    }

    public static string Status =>
        !Enabled ? "Kapalı — araç açılınca kısayol etkinleşir" :
        Running ? $"● Tıklıyor — durdurmak için {Keys[_key]}" :
        !HotkeyOk ? $"{Keys[_key]} başka bir uygulamada kullanılıyor" :
        $"Hazır — başlatmak için {Keys[_key]}";

    // Tool.Apply: araç açılıp kapanınca çağrılır (UI iş parçacığında).
    public static void Apply(bool on)
    {
        if (on)
        {
            Enabled = true;
            if (_win == null) { _win = new HotWin(); _win.Pressed += Toggle; }
            Register();
        }
        else
        {
            Stop();
            if (_win != null) UnregisterHotKey(_win.Handle, 1);
            Enabled = false; HotkeyOk = true;
        }
        Changed?.Invoke();
    }

    static void Register()
    {
        if (_win == null) return;
        UnregisterHotKey(_win.Handle, 1);
        HotkeyOk = RegisterHotKey(_win.Handle, 1, 0x4000 /* MOD_NOREPEAT */, (uint)(0x75 + _key)); // F6 = 0x75
        if (!HotkeyOk) Tray.Log("AutoClicker: kısayol kaydedilemedi " + Keys[_key]);
    }

    static void Toggle() { if (Running) Stop(); else Start(); }

    static void Start()
    {
        if (_cts != null) return;
        var cts = _cts = new CancellationTokenSource();
        new Thread(() => Loop(cts.Token)) { IsBackground = true, Name = "AutoClicker" }.Start();
        Changed?.Invoke();
    }

    static void Stop()
    {
        var c = _cts;
        if (c == null) return;
        _cts = null; c.Cancel();
        Changed?.Invoke();
    }

    // Hassas zamanlama: uzun beklemeleri iptal edilebilir uykuyla, son ~2 ms'yi kısa bekleyişle geçer.
    static void Loop(CancellationToken ct)
    {
        timeBeginPeriod(1);
        try
        {
            var sw = Stopwatch.StartNew();
            double next = 0;
            while (!ct.IsCancellationRequested)
            {
                Click(_btn);
                next += _ms;
                double wait;
                while ((wait = next - sw.Elapsed.TotalMilliseconds) > 0)
                {
                    if (wait > 2) { if (ct.WaitHandle.WaitOne((int)(wait - 1))) return; }
                    else if (ct.IsCancellationRequested) return;
                    else Thread.Yield();
                }
                if (-wait > 200) next = sw.Elapsed.TotalMilliseconds; // çok geride kaldıysa yakalamaya çalışma
            }
        }
        finally { timeEndPeriod(1); }
    }

    static readonly INPUT[] _in = new INPUT[2];
    static void Click(int btn)
    {
        uint down = btn == 1 ? 0x8u : btn == 2 ? 0x20u : 0x2u; // sağ / orta / sol
        _in[0].mi.dwFlags = down; _in[1].mi.dwFlags = down << 1;
        SendInput(2, _in, Marshal.SizeOf<INPUT>());
    }

    // WM_HOTKEY için görünmez mesaj penceresi.
    sealed class HotWin : NativeWindow
    {
        public event Action? Pressed;
        public HotWin() { CreateHandle(new CreateParams { Parent = new IntPtr(-3) /* HWND_MESSAGE */ }); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312) Pressed?.Invoke();
            base.WndProc(ref m);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public MOUSEINPUT mi; }   // type 0 = INPUT_MOUSE

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
}