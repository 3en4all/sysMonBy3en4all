using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace SysMon;

/// Grupa zdarzeń: to samo źródło + ID w dzienniku z ostatnich 24 h.
sealed class Grupa
{
    public required string Klucz, Log, Zrodlo;
    public int Id, Ile, OdPowiadomienia;
    public byte Poziom; // 1 krytyczne, 2 błąd, 3 ostrzeżenie
    public DateTime Ostatnio;
    public DateTime? Powiadomiono;
    public long RekordId;
    public string Tekst = "";
}

/// Zdarzenia Windows z dzienników System i Application: wczytanie 24 h na starcie, potem subskrypcja (EventLogWatcher, bez odpytywania).
sealed class Zdarzenia : IDisposable
{
    static readonly string[] Dzienniki = ["System", "Application"];
    const string Zapytanie = "*[System[(Level=1 or Level=2 or Level=3)]]";
    readonly Dispatcher ui;
    readonly List<EventLogWatcher> obserwatory = [];
    public readonly Dictionary<string, Grupa> Grupy = new();
    /// Wywoływane na wątku okna: (grupa, czy to nowe zdarzenie na żywo).
    public event Action<Grupa?, bool>? Zmiana;

    public Zdarzenia(Dispatcher ui) => this.ui = ui;

    public void Start() => Task.Run(() =>
    {
        var wczytane = new Dictionary<string, Grupa>();
        foreach (var log in Dzienniki)
        {
            try
            {
                var q = new EventLogQuery(log, PathType.LogName, "*[System[(Level=1 or Level=2 or Level=3) and TimeCreated[timediff(@SystemTime) <= 86400000]]]") { ReverseDirection = true };
                using var czytnik = new EventLogReader(q);
                for (EventRecord? r; (r = czytnik.ReadEvent()) != null; )
                    using (r) Dodaj(wczytane, r); // od najnowszych: opis liczony raz, dla pierwszego (najnowszego) w grupie
            }
            catch (EventLogException) { }
        }
        ui.BeginInvoke(() =>
        {
            foreach (var (k, g) in wczytane) Grupy.TryAdd(k, g);
            Zmiana?.Invoke(null, false);
        });
        foreach (var log in Dzienniki)
        {
            try
            {
                var w = new EventLogWatcher(new EventLogQuery(log, PathType.LogName, Zapytanie));
                w.EventRecordWritten += (_, e) =>
                {
                    if (e.EventRecord is not EventRecord r) return;
                    Grupa g;
                    using (r) g = Dodaj(null, r);
                    ui.BeginInvoke(() => Zmiana?.Invoke(Scal(g), true));
                };
                w.Enabled = true;
                obserwatory.Add(w);
            }
            catch (EventLogException) { }
        }
    });

    /// slownik == null → zdarzenie na żywo: grupa uaktualniana na wątku okna.
    Grupa Dodaj(Dictionary<string, Grupa>? slownik, EventRecord r)
    {
        string zrodlo = (r.ProviderName ?? "?").Replace("Microsoft-Windows-", "");
        string klucz = $"{zrodlo}/{r.Id}";
        DateTime czas = r.TimeCreated ?? DateTime.Now;
        byte poziom = r.Level is byte b and >= 1 and <= 3 ? b : (byte)3;
        if (slownik != null && slownik.TryGetValue(klucz, out var jest))
        {
            jest.Ile++;
            jest.Poziom = Math.Min(jest.Poziom, poziom);
            return jest;
        }
        var nowa = new Grupa { Klucz = klucz, Log = r.LogName, Zrodlo = zrodlo, Id = r.Id, Ile = 1, Poziom = poziom, Ostatnio = czas, RekordId = r.RecordId ?? 0, Tekst = Opis(zrodlo, r) };
        if (slownik != null) slownik[klucz] = nowa;
        return nowa;
    }

    /// Zdarzenie na żywo: scalenie z istniejącą grupą (tylko na wątku okna).
    Grupa Scal(Grupa nowa)
    {
        if (!Grupy.TryGetValue(nowa.Klucz, out var g)) return Grupy[nowa.Klucz] = nowa;
        g.Ile++; g.Ostatnio = nowa.Ostatnio; g.RekordId = nowa.RekordId; g.Log = nowa.Log; g.Tekst = nowa.Tekst; g.Poziom = Math.Min(g.Poziom, nowa.Poziom);
        return g;
    }

    /// Opisy po polsku dla znanych zdarzeń; reszta: pierwsza linia opisu z dziennika.
    static string Opis(string zrodlo, EventRecord r)
    {
        string P(int i) => r.Properties.Count > i ? r.Properties[i].Value?.ToString() ?? "" : "";
        string Dysk() => Regex.Match(P(0), @"Harddisk(\d+)", RegexOptions.IgnoreCase) is { Success: true } m ? ModelDysku(int.Parse(m.Groups[1].Value)) : "?";
        string? polski = (zrodlo, r.Id) switch
        {
            ("Kernel-Power", 41) => "Komputer wyłączył się nieoczekiwanie (zgłoszone po ponownym starcie)",
            ("EventLog", 6008) => "Poprzednie zamknięcie systemu było nieoczekiwane",
            ("disk", 7) => $"Dysk {Dysk()}: uszkodzony blok",
            ("disk", 51) => $"Dysk {Dysk()}: błąd przy stronicowaniu (dysk albo kabel)",
            ("disk", 153) => $"Dysk {Dysk()}: operacja ponowiona (dysk odpowiada z opóźnieniem)",
            ("nvlddmkm", 13) => "Sterownik NVIDIA zgłosił błąd (Graphics Exception)",
            ("nvlddmkm", 153) => "Sterownik NVIDIA zgłosił błąd i został przywrócony",
            ("Display", 4101) => "Sterownik ekranu (NVIDIA) przestał odpowiadać i został przywrócony",
            ("Application Error", 1000) => $"Program padł: {P(0)}",
            ("Application Hang", 1002) => $"Program przestał odpowiadać: {P(0)}",
            ("WHEA-Logger", _) => "Błąd sprzętowy (WHEA: procesor, pamięć albo PCIe)",
            _ => null,
        };
        if (polski != null) return polski;
        string opis;
        try { opis = r.FormatDescription() ?? ""; } catch { opis = ""; }
        opis = opis.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "(brak opisu w dzienniku)";
        return $"{zrodlo} {r.Id}: {(opis.Length > 160 ? opis[..160] + "…" : opis)}";
    }

    /// Pełny opis jednego zdarzenia (dymek): czytany dopiero na żądanie, żeby nie trzymać rekordów w pamięci.
    public static string PelnyOpis(Grupa g)
    {
        try
        {
            using var czytnik = new EventLogReader(new EventLogQuery(g.Log, PathType.LogName, $"*[System[EventRecordID={g.RekordId}]]"));
            using var r = czytnik.ReadEvent();
            string tekst = r?.FormatDescription() ?? "(rekord już usunięty z dziennika)";
            return $"{g.Zrodlo} {g.Id} · {g.Log} · {g.Ostatnio:dd.MM HH:mm:ss} · {g.Ile}× w 24 h\n\n{(tekst.Length > 1500 ? tekst[..1500] + "…" : tekst)}";
        }
        catch (Exception e) { return e.Message; }
    }

    public void Przytnij() // grupy starsze niż 24 h znikają
    {
        // shortcut: licznik grupy to liczba od wczytania, nie przesuwne okno 24 h; wystarcza do „ile razy”, dokładne okno wymagałoby trzymania czasów każdego zdarzenia
        foreach (var k in Grupy.Where(kv => kv.Value.Ostatnio < DateTime.Now.AddHours(-24)).Select(kv => kv.Key).ToList()) Grupy.Remove(k);
    }

    public void Dispose() { foreach (var w in obserwatory) w.Dispose(); }

    // --- model dysku z numeru Harddisk N (IOCTL_STORAGE_QUERY_PROPERTY; uchwyt bez praw odczytu wystarcza) ---
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string nazwa, uint dostep, uint wspoldzielenie, IntPtr bezp, uint tworzenie, uint flagi, IntPtr wzor);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint kod, byte[] wej, int dlWej, byte[] wyj, int dlWyj, out int zwrocono, IntPtr ov);
    static readonly Dictionary<int, string> modele = new();

    static string ModelDysku(int n)
    {
        lock (modele)
        {
            if (modele.TryGetValue(n, out var m)) return m;
            m = $"Harddisk{n}";
            try
            {
                using var h = CreateFile($@"\\.\PhysicalDrive{n}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                var wyj = new byte[1024];
                if (!h.IsInvalid && DeviceIoControl(h, 0x2D1400, new byte[12], 12, wyj, wyj.Length, out int ile, IntPtr.Zero))
                {
                    int off = BitConverter.ToInt32(wyj, 16); // STORAGE_DEVICE_DESCRIPTOR.ProductIdOffset
                    if (off > 0 && off < ile)
                        m = System.Text.Encoding.ASCII.GetString(wyj, off, Array.IndexOf(wyj, (byte)0, off) - off).Trim() + $" (Harddisk{n})";
                }
            }
            catch { }
            return modele[n] = m;
        }
    }
}
