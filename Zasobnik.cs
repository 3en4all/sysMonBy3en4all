using System.Runtime.InteropServices;
using System.Windows.Media;

namespace SysMon;

/// Ikona w zasobniku (Shell_NotifyIcon, bez WinForms) i powiadomienia Windows (dymek → na Win10/11 toast).
/// Wariant A: jedna ikona, 3 pionowe paski CPU / GPU / sieć w kolorach kafelków, liczby w dymku.
sealed class Zasobnik : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize; public IntPtr hWnd; public int uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int x, y; public IntPtr hbmMask, hbmColor; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA d);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO ii);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int w, int h, int planes, int bpp, int[]? bity);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    public const int WM_ZASOBNIK = 0x8001; // WM_APP + 1
    public const int WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, NIN_BALLOONUSERCLICK = 0x405;

    readonly IntPtr okno;
    IntPtr ikona;
    bool dodana;
    public bool Stala; // ikona włączona w opcjach (inaczej pojawia się tylko na czas powiadomienia)
    DateTime usunPo = DateTime.MaxValue;

    public Zasobnik(IntPtr okno) => this.okno = okno;

    NOTIFYICONDATA Dane(int flagi) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = okno, uID = 1, uFlags = flagi, uCallbackMessage = WM_ZASOBNIK, hIcon = ikona,
        szTip = "", szInfo = "", szInfoTitle = "",
    };

    /// Odświeżenie paskami (wartości 0–100) i tekstem dymka. Wołane co 1 s, gdy ikona jest stała.
    public void Ustaw(double cpu, double gpu, double siec, string dymek)
    {
        var stara = ikona;
        ikona = Rysuj(cpu, gpu, siec);
        var d = Dane(NIF_ICON | NIF_TIP | NIF_MESSAGE);
        d.szTip = dymek.Length > 127 ? dymek[..127] : dymek;
        Shell_NotifyIcon(dodana ? NIM_MODIFY : NIM_ADD, ref d);
        dodana = true;
        if (stara != IntPtr.Zero) DestroyIcon(stara);
    }

    public void Powiadom(string tytul, string tekst, bool blad)
    {
        if (!dodana) { Ustaw(0, 0, 0, "sysMonBy3en4all"); }
        if (!Stala) usunPo = DateTime.Now.AddSeconds(60); // tymczasowa ikona tylko na czas powiadomienia
        var d = Dane(NIF_INFO);
        d.szInfoTitle = tytul.Length > 63 ? tytul[..63] : tytul;
        d.szInfo = tekst.Length > 255 ? tekst[..255] : tekst;
        d.dwInfoFlags = blad ? 3 : 2; // NIIF_ERROR / NIIF_WARNING; domyślny dźwięk powiadomienia Windows
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    /// Co sekundę: usunięcie tymczasowej ikony po powiadomieniu.
    public void Takt()
    {
        if (!Stala && dodana && DateTime.Now > usunPo) Ukryj();
    }

    public void Ukryj()
    {
        if (dodana) { var d = Dane(0); Shell_NotifyIcon(NIM_DELETE, ref d); }
        dodana = false; usunPo = DateTime.MaxValue;
        if (ikona != IntPtr.Zero) { DestroyIcon(ikona); ikona = IntPtr.Zero; }
    }

    /// Po restarcie Eksploratora (komunikat TaskbarCreated) ikonę trzeba dodać od nowa.
    public void PoRestarcieEksploratora() { if (dodana) { dodana = false; var d = Dane(NIF_ICON | NIF_MESSAGE); Shell_NotifyIcon(NIM_ADD, ref d); dodana = true; } }

    static IntPtr Rysuj(double cpu, double gpu, double siec)
    {
        int s = Math.Max(16, GetSystemMetrics(49)); // SM_CXSMICON: 16 przy 100%, 24 przy 150%
        var px = new int[s * s];
        Color[] kolory = [Kolory.Cpu, Kolory.Gpu, Kolory.Siec];
        double[] wart = [cpu, gpu, siec];
        int szer = s / 3;
        for (int k = 0; k < 3; k++)
        {
            int wys = (int)Math.Round(Math.Clamp(wart[k], 0, 100) / 100 * s);
            uint c = (uint)(kolory[k].R << 16 | kolory[k].G << 8 | kolory[k].B);
            for (int y = 0; y < s; y++)
                for (int x = k * szer; x < k * szer + szer - 1; x++)
                    px[y * s + x] = (int)((y >= s - wys ? 0xFF000000u : 0x50000000u) | c); // tło paska półprzezroczyste, żeby ikona była widoczna przy 0
        }
        IntPtr kolor = CreateBitmap(s, s, 1, 32, px), maska = CreateBitmap(s, s, 1, 1, new int[((s + 15) / 16 * 2 * s + 3) / 4]); // zerowa maska; przezroczystość z kanału alfa
        var ii = new ICONINFO { fIcon = true, hbmColor = kolor, hbmMask = maska };
        IntPtr h = CreateIconIndirect(ref ii);
        DeleteObject(kolor); DeleteObject(maska);
        return h;
    }

    public void Dispose() => Ukryj();
}
