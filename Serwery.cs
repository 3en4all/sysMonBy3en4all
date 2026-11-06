using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SysMon;

/// Serwer = proces z co najmniej jednym portem TCP w stanie Listen.
sealed record Serwer(int Pid, string Proces, string Nazwa, List<(string Adres, int Port)> Porty, bool WSieci, string LiniaPolecen, string? Folder)
{
    public string PortyTekst => Porty.Count <= 3 ? string.Join(", ", Porty.Select(p => p.Port).Distinct())
        : string.Join(", ", Porty.Select(p => p.Port).Distinct().Take(2)) + $" +{Porty.Select(p => p.Port).Distinct().Count() - 2}";
    /// Klucz do wykrywania zniknięcia: ta sama usługa po restarcie ma inny PID, ale ten sam proces i porty.
    public IEnumerable<string> Klucze => Porty.Select(p => $"{Proces}:{p.Port}").Distinct();
}

/// Porty nasłuchujące (GetExtendedTcpTable, IPv4 + IPv6) z procesem i nazwą wziętą z linii poleceń.
sealed class Serwery
{
    [DllImport("iphlpapi.dll")] static extern int GetExtendedTcpTable(IntPtr tabela, ref int rozmiar, bool sortuj, int rodzina, int klasa, int zarezerw);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int dostep, bool dziedzicz, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flagi, StringBuilder sb, ref int rozmiar);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int klasa, IntPtr bufor, int dlugosc, out int potrzeba);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CommandLineToArgvW(string linia, out int ile);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    const int AF_INET = 2, AF_INET6 = 23, TCP_TABLE_OWNER_PID_LISTENER = 3, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int ProcessBasicInformation = 0, ProcessCommandLineInformation = 60;

    /// Procesy systemowe: chowane pod licznikiem (porty 135, 139, 445, 5040, 5357, 7680, 49664+ itd.).
    static readonly HashSet<string> Systemowe = new(StringComparer.OrdinalIgnoreCase)
        { "System", "svchost", "lsass", "wininit", "services", "spoolsv", "Idle", "Registry", "smss", "csrss", "winlogon", "MsMpEng", "NisSrv" };
    static readonly Dictionary<string, string> Przyjazne = new(StringComparer.OrdinalIgnoreCase)
        { ["com.docker.backend"] = "Docker Desktop", ["vmware-authd"] = "VMware (autoryzacja)", ["GoogleDriveFS"] = "Google Drive",
          ["usbipd"] = "usbipd-win (USB przez sieć)", ["ss_conn_service"] = "SteelSeries", ["ss_conn_service2"] = "SteelSeries" };
    static readonly HashSet<string> OgolnePliki = new(StringComparer.OrdinalIgnoreCase) { "index", "main", "server", "app", "cli", "run", "start", "__main__" };

    readonly Dictionary<int, (string Proces, string Linia, string? Exe)> pamiec = new(); // linia poleceń raz na PID
    readonly Dictionary<int, string> ollama = new(); // port → opis modelu z /api/ps
    HttpClient? http;

    public List<Serwer> Lista = [];
    public List<string> Ukryte = []; // opisy schowanych (systemowe, runnery Ollamy) do dymka

    public void Odczyt(IReadOnlyDictionary<long, string> nazwy)
    {
        var porty = new Dictionary<int, List<(string, int)>>();
        Tabela(AF_INET, 24, porty, (p, o) => (new IPAddress((uint)Marshal.ReadInt32(p, o + 4)).ToString(), Port(Marshal.ReadInt32(p, o + 8)), Marshal.ReadInt32(p, o + 20)));
        Tabela(AF_INET6, 56, porty, (p, o) =>
        {
            var b = new byte[16]; Marshal.Copy(p + o, b, 0, 16);
            return (new IPAddress(b).ToString(), Port(Marshal.ReadInt32(p, o + 20)), Marshal.ReadInt32(p, o + 52));
        });

        foreach (int stary in pamiec.Keys.Where(k => !porty.ContainsKey(k)).ToList()) pamiec.Remove(stary);
        var lista = new List<Serwer>(); var ukryte = new List<string>();
        foreach (var (pid, lp) in porty)
        {
            string proces = nazwy.TryGetValue(pid, out var n) ? n : pid == 4 ? "System" : "?";
            if (!pamiec.TryGetValue(pid, out var info) || info.Proces != proces) // PID użyty ponownie przez inny program
                pamiec[pid] = info = (proces, LiniaPolecen(pid), SciezkaExe(pid));
            lp.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            bool runner = proces.Equals("ollama", StringComparison.OrdinalIgnoreCase) && info.Linia.Contains(" runner", StringComparison.OrdinalIgnoreCase);
            if (pid <= 4 || Systemowe.Contains(proces) || runner)
            {
                ukryte.Add($"{(runner ? "ollama runner" : proces)}: {string.Join(", ", lp.Select(x => x.Item2).Distinct())}");
                continue;
            }
            bool wSieci = lp.Any(x => !IPAddress.IsLoopback(IPAddress.Parse(x.Item1)));
            var (nazwa, folder) = Opis(proces, info.Linia, info.Exe);
            if (proces.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                nazwa = "Ollama · " + string.Join(" · ", lp.Select(x => x.Item2).Distinct().Select(p => ollama.TryGetValue(p, out var m) ? m : "…"));
            lista.Add(new Serwer(pid, proces, nazwa, lp, wSieci, info.Linia, folder));
        }
        // widoczne w sieci na górze: nie mogą zniknąć pod „… i N więcej”
        lista.Sort((a, b) => a.WSieci != b.WSieci ? b.WSieci.CompareTo(a.WSieci) : string.Compare(a.Nazwa, b.Nazwa, StringComparison.CurrentCultureIgnoreCase));
        ukryte.Sort(StringComparer.CurrentCultureIgnoreCase);
        (Lista, Ukryte) = (lista, ukryte);

        foreach (var s in lista.Where(s => s.Proces.Equals("ollama", StringComparison.OrdinalIgnoreCase)))
            foreach (var (adres, port) in s.Porty) _ = OllamaPs(adres, port); // w tle; wynik pojawi się przy następnym odczycie
    }

    static int Port(int surowy) => ((surowy & 0xFF) << 8) | ((surowy >> 8) & 0xFF); // kolejność sieciowa w młodszych 16 bitach

    static void Tabela(int rodzina, int dlWiersza, Dictionary<int, List<(string, int)>> wynik, Func<IntPtr, int, (string Adres, int Port, int Pid)> wiersz)
    {
        int rozmiar = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref rozmiar, false, rodzina, TCP_TABLE_OWNER_PID_LISTENER, 0);
        for (int proba = 0; proba < 3; proba++)
        {
            IntPtr bufor = Marshal.AllocHGlobal(rozmiar += 1024);
            try
            {
                int st = GetExtendedTcpTable(bufor, ref rozmiar, false, rodzina, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (st == 122) continue; // ERROR_INSUFFICIENT_BUFFER: tabela urosła między wywołaniami
                if (st != 0) return;
                int ile = Marshal.ReadInt32(bufor);
                for (int i = 0; i < ile; i++)
                {
                    var (adres, port, pid) = wiersz(bufor, 4 + i * dlWiersza);
                    if (!wynik.TryGetValue(pid, out var l)) wynik[pid] = l = [];
                    if (!l.Contains((adres, port))) l.Add((adres, port));
                }
                return;
            }
            finally { Marshal.FreeHGlobal(bufor); }
        }
    }

    async Task OllamaPs(string adres, int port)
    {
        if (adres is "0.0.0.0" or "::") adres = "127.0.0.1";
        try
        {
            http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"http://{(adres.Contains(':') ? $"[{adres}]" : adres)}:{port}/api/ps"));
            var modele = doc.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => $"{m.GetProperty("name").GetString()} {m.GetProperty("size_vram").GetDouble() / (1 << 30):0.0} GB VRAM").ToList();
            ollama[port] = modele.Count > 0 ? string.Join(", ", modele) : "bez modelu";
        }
        catch { ollama[port] = "brak odpowiedzi /api/ps"; }
    }

    /// Nazwa czytelna dla człowieka: skrypt / moduł / paczka z linii poleceń, nie zgadywana po porcie.
    internal static (string Nazwa, string? Folder) Opis(string proces, string linia, string? exe)
    {
        string? folderExe = exe != null ? Path.GetDirectoryName(exe) : null;
        if (Przyjazne.TryGetValue(proces, out var p)) return (p, folderExe);
        var t = Argumenty(linia).Skip(1).ToList();
        int m = t.IndexOf("-m");
        if (m >= 0 && m + 1 < t.Count)
        {
            string modul = t[m + 1] + (m + 2 < t.Count && !t[m + 2].StartsWith('-') ? " " + t[m + 2] : "");
            return ($"{proces} · {modul}", folderExe);
        }
        string? arg = t.FirstOrDefault(a => !a.StartsWith('-') && (a.Contains('\\') || a.Contains('/') || Path.HasExtension(a)));
        if (arg == null) return (proces, folderExe);
        string? folder = null;
        try { if (File.Exists(arg)) folder = Path.GetDirectoryName(Path.GetFullPath(arg)); } catch { }
        var nm = Regex.Match(arg, @"node_modules[\\/]+(?:@[^\\/]+[\\/]+)?([^\\/]+)");
        if (nm.Success) return ($"{proces} · {nm.Groups[1].Value}", folder ?? folderExe);
        string plik = Path.GetFileName(arg.TrimEnd('\\', '/'));
        if (OgolnePliki.Contains(Path.GetFileNameWithoutExtension(plik)) && Path.GetFileName(Path.GetDirectoryName(arg.TrimEnd('\\', '/'))) is { Length: > 0 } rodzic)
            plik = rodzic + "\\" + plik;
        return ($"{proces} · {plik}", folder ?? folderExe);
    }

    internal static string[] Argumenty(string linia)
    {
        if (string.IsNullOrWhiteSpace(linia)) return [];
        IntPtr a = CommandLineToArgvW(linia, out int ile);
        if (a == IntPtr.Zero) return [];
        try { return Enumerable.Range(0, ile).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(a, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(a); }
    }

    static string LiniaPolecen(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            int dl = 4096;
            for (int proba = 0; proba < 2; proba++)
            {
                IntPtr b = Marshal.AllocHGlobal(dl);
                try
                {
                    int st = NtQueryInformationProcess(h, ProcessCommandLineInformation, b, dl, out int potrzeba);
                    if (st == 0) return Marshal.PtrToStringUni(Marshal.ReadIntPtr(b, 8), (Marshal.ReadInt16(b) & 0xFFFF) / 2); // UNICODE_STRING
                    if (potrzeba <= dl) return "";
                    dl = potrzeba;
                }
                finally { Marshal.FreeHGlobal(b); }
            }
            return "";
        }
        finally { CloseHandle(h); }
    }

    static string? SciezkaExe(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try { var sb = new StringBuilder(1024); int r = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref r) ? sb.ToString() : null; }
        finally { CloseHandle(h); }
    }

    static int Rodzic(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return 0;
        IntPtr b = Marshal.AllocHGlobal(48);
        try { return NtQueryInformationProcess(h, ProcessBasicInformation, b, 48, out _) == 0 ? (int)Marshal.ReadInt64(b, 40) : 0; } // InheritedFromUniqueProcessId
        finally { Marshal.FreeHGlobal(b); CloseHandle(h); }
    }

    /// Plik logu serwera: argument .log w linii poleceń, przekierowanie w skrypcie .ps1/.cmd rodzica, albo znane miejsca (Ollama, Docker).
    internal static string? ZnajdzLog(Serwer s)
    {
        static string? LogZLinii(string linia) => Argumenty(linia).Select(a => a.Trim('"'))
            .FirstOrDefault(a => a.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        static string? LogZeSkryptu(string linia)
        {
            var skrypt = Argumenty(linia).FirstOrDefault(a => Regex.IsMatch(a, @"\.(ps1|cmd|bat)$", RegexOptions.IgnoreCase) && File.Exists(a));
            if (skrypt == null || new FileInfo(skrypt).Length > 1 << 20) return null;
            var m = Regex.Match(File.ReadAllText(skrypt), @">>?\s*""?([A-Za-z]:\\[^""\r\n]+?\.log)""?", RegexOptions.IgnoreCase);
            return m.Success && File.Exists(m.Groups[1].Value) ? m.Groups[1].Value : null;
        }
        if (LogZLinii(s.LiniaPolecen) is string l) return l;
        int rodzic = Rodzic(s.Pid);
        if (rodzic > 4 && LiniaPolecen(rodzic) is { Length: > 0 } lr && (LogZLinii(lr) ?? LogZeSkryptu(lr)) is string l2) return l2;
        string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? znany = s.Proces.ToLowerInvariant() switch
        {
            "ollama" => Path.Combine(lad, "Ollama", "server.log"),
            "com.docker.backend" => Path.Combine(lad, "Docker", "log", "host", "com.docker.backend.exe.log"),
            _ => null,
        };
        return znany != null && File.Exists(znany) ? znany : null;
    }

    /// Osobne okno PowerShell, tylko do odczytu: log na żywo albo ruch i zasoby procesu. Zamknięcie nie zatrzymuje serwera.
    internal static void Podglad(Serwer s)
    {
        static string Q(string t) => "'" + t.Replace("'", "''") + "'";
        string? log = ZnajdzLog(s);
        string porty = string.Join(", ", s.Porty.Select(p => $"{p.Adres}:{p.Port}"));
        var sb = new StringBuilder();
        sb.AppendLine($"$Host.UI.RawUI.WindowTitle = {Q("3EN - Podgląd " + s.Nazwa)}");
        sb.AppendLine($"Write-Host {Q($"Oglądasz: {s.Nazwa} (PID {s.Pid}, {porty})")} -ForegroundColor Cyan");
        if (log != null)
        {
            sb.AppendLine($"Write-Host {Q("Źródło: log " + log)}");
            sb.AppendLine("Write-Host 'Zamknięcie tego okna NIE zatrzymuje serwera.' -ForegroundColor Yellow");
            sb.AppendLine($"Get-Content -LiteralPath {Q(log)} -Wait -Tail 50");
        }
        else
        {
            sb.AppendLine("Write-Host 'Źródło: połączenia i zasoby procesu (serwer nie pisze znanego logu)'");
            sb.AppendLine("Write-Host 'Zamknięcie tego okna NIE zatrzymuje serwera.' -ForegroundColor Yellow");
            sb.AppendLine($$"""
                while ($true) {
                  $p = Get-Process -Id {{s.Pid}} -ErrorAction SilentlyContinue
                  if (-not $p) { Write-Host 'Proces zakończył działanie.' -ForegroundColor Red; break }
                  $c = @(Get-NetTCPConnection -OwningProcess {{s.Pid}} -State Established -ErrorAction SilentlyContinue)
                  Write-Host ('{0:HH:mm:ss}  CPU łącznie {1:N1} s  RAM {2:N0} MB  połączenia {3}' -f (Get-Date), $p.CPU, ($p.WorkingSet64 / 1MB), $c.Count)
                  $c | Select-Object -First 5 | ForEach-Object { Write-Host ('    {0}:{1} <- {2}:{3}' -f $_.LocalAddress, $_.LocalPort, $_.RemoteAddress, $_.RemotePort) -ForegroundColor DarkGray }
                  Start-Sleep 3
                }
                """);
        }
        string kod = Convert.ToBase64String(Encoding.Unicode.GetBytes(sb.ToString()));
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoExit -NoProfile -ExecutionPolicy Bypass -EncodedCommand {kod}") { UseShellExecute = true });
    }

    /// Przez explorer.exe, żeby przeglądarka/folder nie dziedziczyły uprawnień administratora monitora.
    internal static void OtworzBezAdmina(string cel) => Process.Start(new ProcessStartInfo("explorer.exe", "\"" + cel + "\"") { UseShellExecute = false });

    internal static string Adres(Serwer s)
    {
        var (adres, port) = s.Porty.FirstOrDefault(p => IPAddress.IsLoopback(IPAddress.Parse(p.Adres)) || p.Adres is "0.0.0.0" or "::");
        if (port == 0) (adres, port) = s.Porty[0];
        if (adres is "0.0.0.0" or "::" or "::1") adres = "localhost";
        return $"http://{(adres.Contains(':') ? $"[{adres}]" : adres)}:{port}/";
    }

    /// Samotest nazw z linii poleceń na przykładach z 3EN035.
    internal static string? Samotest()
    {
        (string proces, string linia, string oczekiwane)[] p =
        [
            ("node", @"""node""   ""C:\Users\x\AppData\Roaming\npm\\node_modules\openclaw\openclaw.mjs"" gateway --port 18789", "node · openclaw"),
            ("node", @"""C:\Program Files\nodejs\node.exe"" D:\a\n8n\node_modules\n8n\bin\n8n start ", "node · n8n"),
            ("node", @"""C:\Program Files\nodejs\node.exe"" E:\x\control-panel\server.mjs", @"node · control-panel\server.mjs"),
            ("python", @"""C:\P\python.exe"" -m uvicorn app.api:app --host 127.0.0.1 --port 8788", "python · uvicorn app.api:app"),
            ("pythonw", @"""C:\P\pythonw.exe"" ""D:\P\3en-security-privacy-web.py""", "pythonw · 3en-security-privacy-web.py"),
            ("com.docker.backend", @"""C:\D\com.docker.backend.exe"" services", "Docker Desktop"),
        ];
        foreach (var (proces, linia, ocz) in p)
            if (Opis(proces, linia, null).Nazwa is var n && n != ocz) return $"nazwa serwera: „{n}” zamiast „{ocz}”";
        if (Port(unchecked((int)0x0000_1F90)) != 0x901F) return "zamiana bajtów portu";
        return null;
    }
}
