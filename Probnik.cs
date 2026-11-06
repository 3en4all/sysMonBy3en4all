using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SysMon;

/// Odczyt CPU i RAM: całość systemu + suma na nazwę procesu (jak grupowanie w Menedżerze zadań).
/// Jedno wywołanie NtQuerySystemInformation na odczyt, bez otwierania uchwytów procesów.
sealed class Probnik
{
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int klasa, IntPtr bufor, int dlugosc, out int potrzeba);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long bezczynny, out long jadro, out long uzytkownik);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr proces, out IO_COUNTERS io);

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong OpOdczyt, OpZapis, OpInne, Odczyt, Zapis, Inne; }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    const int SystemProcessInformation = 5;
    const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    // Przesunięcia w SYSTEM_PROCESS_INFORMATION (x64)
    const int OffPrywatnyWs = 8, OffStart = 32, OffUser = 40, OffKernel = 48, OffNazwaDl = 56, OffNazwaBuf = 64, OffPid = 80,
        OffOdczyt = 232, OffZapis = 240, OffInne = 248; // IO_COUNTERS: Read/Write/OtherTransferCount

    IntPtr bufor = Marshal.AllocHGlobal(1 << 20);
    int dlugoscBufora = 1 << 20;
    long pBezczynny, pJadro, pUzytkownik, pChwila;
    Dictionary<(long pid, long start), (long Czas, long Rw, long Inne)> poprz = new(), biez = new();

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint dostep, bool dziedzicz, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr proces, int flagi, System.Text.StringBuilder tekst, ref int dlugosc);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr uchwyt);
    static string SciezkaProcesu(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return "niedostępna";
        try { var tekst = new System.Text.StringBuilder(32768); int dl = tekst.Capacity; return QueryFullProcessImageName(h, 0, tekst, ref dl) ? tekst.ToString() : "niedostępna"; }
        finally { CloseHandle(h); }
    }
    public sealed record Proces(long Pid, string Nazwa, double Cpu, double Pamiec, string Sciezka);
    public readonly List<Proces> Procesy = [];
    readonly Dictionary<(long, long), string> sciezki = new();
    public double CpuProcent;
    public double RamUzyte, RamCalkowite; // bajty
    public readonly Dictionary<string, double> Cpu = new(), Ram = new(); // % całego CPU / bajty prywatnego zestawu roboczego
    // shortcut: I/O procesu nie rozróżnia dysku i sieci. Odczyt+zapis ≈ pliki (dysk + pamięć podręczna), „inne” ≈ IOCTL, w tym gniazda sieciowe.
    // Przybliżenie uzgodnione w HANDOFF; dokładnie dopiero przez ETW.
    public readonly Dictionary<string, double> Dysk = new(), Siec = new(); // bajty/s
    public readonly Dictionary<long, string> Nazwy = new(); // PID → nazwa, dla liczników GPU
    (long Odczyt, long Zapis, long Inne) wlasneIo; // dla samotestu

    public void Odczyt()
    {
        GetSystemTimes(out long bezcz, out long jadro, out long uzytk); // jądro zawiera czas bezczynności
        long dSys = jadro - pJadro + (uzytk - pUzytkownik);
        bool pierwszy = pJadro == 0;
        CpuProcent = pierwszy || dSys <= 0 ? 0 : Math.Clamp(100.0 * (dSys - (bezcz - pBezczynny)) / dSys, 0, 100);
        (pBezczynny, pJadro, pUzytkownik) = (bezcz, jadro, uzytk);

        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m)) (RamCalkowite, RamUzyte) = (m.ullTotalPhys, m.ullTotalPhys - m.ullAvailPhys);

        int st;
        while ((st = NtQuerySystemInformation(SystemProcessInformation, bufor, dlugoscBufora, out int potrzeba)) == STATUS_INFO_LENGTH_MISMATCH)
        {
            Marshal.FreeHGlobal(bufor);
            dlugoscBufora = Math.Max(potrzeba, dlugoscBufora) * 2;
            bufor = Marshal.AllocHGlobal(dlugoscBufora);
        }
        if (st != 0) return; // zostają wartości z poprzedniego odczytu

        long chwila = Stopwatch.GetTimestamp();
        double sekundy = (chwila - pChwila) / (double)Stopwatch.Frequency;
        pChwila = chwila;
        Procesy.Clear();
        Cpu.Clear(); Ram.Clear(); Dysk.Clear(); Siec.Clear(); Nazwy.Clear(); biez.Clear();
        for (int off = 0; ; )
        {
            IntPtr p = bufor + off;
            long pid = Marshal.ReadInt64(p, OffPid);
            if (pid != 0) // 0 = proces bezczynności
            {
                int dl = Marshal.ReadInt16(p, OffNazwaDl) & 0xFFFF;
                IntPtr tekst = Marshal.ReadIntPtr(p, OffNazwaBuf);
                string nazwa = tekst == IntPtr.Zero ? "System" : Marshal.PtrToStringUni(tekst, dl / 2);
                if (nazwa.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) nazwa = nazwa[..^4];
                Nazwy[pid] = nazwa;

                var klucz = (pid, Marshal.ReadInt64(p, OffStart)); // czas startu odróżnia ponownie użyty PID
                long czas = Marshal.ReadInt64(p, OffUser) + Marshal.ReadInt64(p, OffKernel);
                long odczyt = Marshal.ReadInt64(p, OffOdczyt), zapis = Marshal.ReadInt64(p, OffZapis), inne = Marshal.ReadInt64(p, OffInne);
                if (pid == Environment.ProcessId) wlasneIo = (odczyt, zapis, inne);
                (long Czas, long Rw, long Inne) teraz = (czas, odczyt + zapis, inne);
                biez[klucz] = teraz;
                if (!pierwszy && dSys > 0 && poprz.TryGetValue(klucz, out var stary))
                {
                    Dodaj(Cpu, nazwa, 100.0 * (teraz.Czas - stary.Czas) / dSys);
                    Dodaj(Dysk, nazwa, (teraz.Rw - stary.Rw) / sekundy);
                    Dodaj(Siec, nazwa, (inne - stary.Inne) / sekundy);
                }
                double pamiec = Marshal.ReadInt64(p, OffPrywatnyWs);
                double cpu = !pierwszy && dSys > 0 && poprz.TryGetValue(klucz, out var poprzedni) ? 100.0 * (czas - poprzedni.Czas) / dSys : 0;
                if (!sciezki.TryGetValue(klucz, out string? sciezka))
                {
                    try { sciezka = SciezkaProcesu((int)pid); }
                    catch { sciezka = "niedostępna"; }
                    sciezki[klucz] = sciezka;
                }
                Procesy.Add(new(pid, nazwa, cpu, pamiec / (1024 * 1024), sciezka));
                Dodaj(Ram, nazwa, pamiec);
            }
            int nast = Marshal.ReadInt32(p, 0);
            if (nast == 0) break;
            off += nast;
        }
        foreach (var k in sciezki.Keys.Where(k => !biez.ContainsKey(k)).ToArray()) sciezki.Remove(k);
        (poprz, biez) = (biez, poprz);
    }

    internal static void Dodaj(Dictionary<string, double> d, string k, double v) =>
        d[k] = d.TryGetValue(k, out double s) ? s + v : v;

    public static List<(string Nazwa, double Wartosc)> Top5(Dictionary<string, double> d) =>
        d.OrderByDescending(kv => kv.Value).Take(5).Select(kv => (kv.Key, kv.Value)).ToList();

    /// Samotest przesunięć struktury: własny proces musi się znaleźć z sensownym RAM, suma CPU nie może przekroczyć 100%.
    public static string? Samotest()
    {
        var p = new Probnik();
        p.Odczyt();
        Thread.Sleep(300);
        p.Odczyt();
        string ja = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath!);
        if (!p.Ram.TryGetValue(ja, out double ram) || ram < 1e6 || ram > 1e10) return $"nie znaleziono własnego procesu '{ja}' z poprawnym RAM ({ram:0})";
        if (p.RamCalkowite <= 0 || p.RamUzyte > p.RamCalkowite) return "zły odczyt pamięci systemu";
        double suma = p.Cpu.Values.Sum();
        if (suma < 0 || suma > 101) return $"suma CPU procesów {suma:0.0}% poza zakresem";
        // liczniki I/O z migawki muszą zgadzać się z API dla własnego procesu (API czytane później → ≥, różnica mała)
        GetProcessIoCounters(Process.GetCurrentProcess().Handle, out var io);
        var w = p.wlasneIo;
        bool zgodne(long snap, ulong api) => snap >= 0 && (ulong)snap <= api && api - (ulong)snap < 50_000_000;
        if (w.Odczyt + w.Zapis + w.Inne <= 0 || !zgodne(w.Odczyt, io.Odczyt) || !zgodne(w.Zapis, io.Zapis) || !zgodne(w.Inne, io.Inne))
            return $"liczniki I/O migawki ({w}) niezgodne z GetProcessIoCounters ({io.Odczyt}, {io.Zapis}, {io.Inne})";
        return GpuProbnik.Samotest() ?? SiecDyskiProbnik.Samotest() ?? Serwery.Samotest();
    }
}
