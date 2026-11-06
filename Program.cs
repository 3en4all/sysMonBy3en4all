using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SysMon;

static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int RegisterWindowMessage(string nazwa);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    internal static readonly int WM_POKAZ = RegisterWindowMessage("sysMonBy3en4all_pokaz"), WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");
    static Mutex? jedenEgzemplarz;

    [STAThread]
    static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
        if (args.Length == 2 && args[0] == "--czujniki") { Czujniki.Zrzut(args[1]); return; }
        // Jeden egzemplarz: drugi start (np. ręczny przy autostarcie) tylko pokazuje działające okno.
        jedenEgzemplarz = new Mutex(true, @"Local\sysMonBy3en4all", out bool pierwszy);
        if (!pierwszy) { PostMessage((IntPtr)0xFFFF, WM_POKAZ, IntPtr.Zero, IntPtr.Zero); return; } // HWND_BROADCAST
        var app = new Application();
        if ((Probnik.Samotest() ?? Alerty.Samotest()) is string blad)
        {
            MessageBox.Show("Samotest odczytu procesów nie przeszedł: " + blad, "sysMonBy3en4all", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        app.Run(new OknoGlowne());
        GC.KeepAlive(jedenEgzemplarz);
    }
}

static class Kolory
{
    public static readonly Brush Tlo0 = B(0x1A1A19), Tlo1 = B(0x242423), Tekst = B(0xF1EFE8), Drugi = B(0xB4B2A9), Szary = B(0x888780),
        Akcent = B(0x85B7EB), Ostrzezenie = B(0xEF9F27), Blad = B(0xF09595),
        Ramka = Zamroz(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)));
    public static readonly Color Cpu = Color.FromRgb(0x1D, 0x9E, 0x75), Ram = Color.FromRgb(0xD8, 0x5A, 0x30),
        Gpu = Color.FromRgb(0x7F, 0x77, 0xDD), Vram = Color.FromRgb(0xD4, 0x53, 0x7E),
        Siec = Color.FromRgb(0x37, 0x8A, 0xDD), Dyski = Color.FromRgb(0xBA, 0x75, 0x17),
        Temperatury = Color.FromRgb(0xE2, 0x4B, 0x4A), Wentylatory = Color.FromRgb(0x88, 0x87, 0x80);
    public static readonly Brush CpuP = Zamroz(new SolidColorBrush(Cpu));
    static Brush B(int rgb) => Zamroz(new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)));
    public static Brush Zamroz(SolidColorBrush b) { b.Freeze(); return b; }
}

sealed class OknoGlowne : Window
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    const int WierszeListy = 10; // stała wysokość paneli: okno nie skacze, gdy zmienia się liczba serwerów/zdarzeń
    readonly Ustawienia ust = Ustawienia.Wczytaj();
    readonly Probnik probnik = new();
    readonly GpuProbnik gpuProbnik = new();
    readonly Kafelek cpu = new("CPU", Kolory.Cpu), ram = new("RAM", Kolory.Ram),
        gpu = new("GPU", Kolory.Gpu, 31), vram = new("VRAM", Kolory.Vram, 31), // GPU co 2 s → 31 punktów = 60 s
        siec = new("Sieć", Kolory.Siec, skalaMin: 1e6), dyski = new("Dyski", Kolory.Dyski, skalaMin: 1 << 20); // autoskala od 1 Mb/s / 1 MB/s
    readonly Kafelek temperatury = new("Temperatury", Kolory.Temperatury, 31), wentylatory = new("Wentylatory", Kolory.Wentylatory, 31, skalaMin: 1000);
    readonly SiecDyskiProbnik siecDyski = new();
    readonly Task<Czujniki> czujniki = Task.Run(() => new Czujniki()); // otwarcie LHM trwa ok. 1–3 s — okno nie czeka
    readonly Serwery serwery = new();
    readonly Zdarzenia zdarzenia;
    Zasobnik? zasobnik;
    readonly ListaPanel panelSerwerow = new("Serwery · klik: podgląd, prawy: akcje"), panelZdarzen = new("Zdarzenia · 24 h · prawy klik: wycisz");
    readonly Pdh rdzeniePdh = new(@"\Processor Information(*)\% Processor Time");
    readonly List<(int[] Watki, Wykres Wykres, TextBlock Etykieta)> rdzenie = [];
    readonly WrapPanel wykresyRdzeni = new();
    readonly TextBlock szczegolyCpuWartosc = new() { Foreground = Kolory.Tekst, FontSize = 15 };
    readonly DataGrid szczegolyCpuProcesy = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = true, RowHeight = 22, HeadersVisibility = DataGridHeadersVisibility.Column, Background = Kolory.Tlo1, Foreground = Kolory.Tekst, RowBackground = Kolory.Tlo1, AlternatingRowBackground = Kolory.Tlo0, BorderBrush = Kolory.Ramka, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HorizontalGridLinesBrush = Kolory.Ramka, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, ClipToBounds = true };
    string sortProcesow = "Cpu";
    System.ComponentModel.ListSortDirection kierunekProcesow = System.ComponentModel.ListSortDirection.Descending;
    readonly Border szczegolyCpu = new() { Background = Kolory.Tlo1, CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(6, 3, 6, 6), Visibility = Visibility.Collapsed };
    int takt, taktSerwerow;
    readonly UniformGrid siatka = new() { Columns = 4, Margin = new Thickness(3, 0, 3, 0) };
    readonly DispatcherTimer zegar = new() { Interval = TimeSpan.FromSeconds(1) }, zegarSerwerow = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly string nazwaCpu;
    string cpuWaty = "", podpisSerwerow = "";
    Dictionary<string, string>? poprzednieSerwery; // klucz proces:port → opis, do powiadomienia o zniknięciu
    readonly TextBlock pinezka;
    readonly WrapPanel panelOpcji = new() { Margin = new Thickness(9, 0, 6, 4), Visibility = Visibility.Collapsed };
    CheckBox? autostart;
    readonly Alerty alerty = new();
    readonly TextBlock czasPracy = new() { FontSize = 11, Foreground = Kolory.Szary, Margin = new Thickness(4, 4, 0, 2) };
    readonly DateTime startSystemu = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
    string przyczynaStartu = "", zdrowieSz = "", zdrowieDymek = "";

    bool Zminimalizowane => WindowState == WindowState.Minimized;

    public OknoGlowne()
    {
        Title = "sysMonBy3en4all · " + Environment.MachineName;
        Width = 960; SizeToContent = SizeToContent.Height;
        Background = Kolory.Tlo0;
        FontFamily = new FontFamily("Segoe UI"); FontSize = 12;
        Topmost = ust.NaWierzchu;
        if (ust.Lewo is double l && ust.Gora is double t && ust.Szerokosc is double sz &&
            l + 60 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth && l + sz > SystemParameters.VirtualScreenLeft + 60 &&
            t >= SystemParameters.VirtualScreenTop - 10 && t + 40 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        { WindowStartupLocation = WindowStartupLocation.Manual; Left = l; Top = t; Width = sz; } // tylko gdy okno trafi na istniejący ekran
        if (ust.Zmaksymalizowane) WindowState = WindowState.Maximized;

        // pasek: pinezka i opcje
        pinezka = Ikona("", "Zawsze na wierzchu", () => UstawNaWierzchu(!ust.NaWierzchu));
        var pasek = new DockPanel { LastChildFill = false, Margin = new Thickness(6, 2, 6, 0) };
        foreach (var e in new[] { Ikona("", "Opcje", () => panelOpcji.Visibility = panelOpcji.IsVisible ? Visibility.Collapsed : Visibility.Visible), pinezka })
        { DockPanel.SetDock(e, Dock.Right); pasek.Children.Add(e); }
        pasek.Children.Add(czasPracy);
        Task.Run(() => PrzyczynaStartu(startSystemu)).ContinueWith(t => przyczynaStartu = t.IsCompletedSuccessfully ? t.Result : "", TaskScheduler.Default);
        UstawNaWierzchu(ust.NaWierzchu);
        BudujOpcje();

        foreach (var k in new[] { cpu, gpu, ram, vram, siec, dyski, temperatury, wentylatory }) siatka.Children.Add(k);
        var dol = new Grid { Margin = new Thickness(3, 0, 3, 3) };
        dol.ColumnDefinitions.Add(new ColumnDefinition());
        dol.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(panelZdarzen, 1);
        dol.Children.Add(panelSerwerow); dol.Children.Add(panelZdarzen);
        panelSerwerow.Wiersze.MinHeight = panelZdarzen.Wiersze.MinHeight = WierszeListy * ListaPanel.WysWiersza;

        var naglowekCpu = new DockPanel();
        var zamknijCpu = new Button { Content = "Zamknij", Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Right };
        zamknijCpu.Click += (_, _) => szczegolyCpu.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(zamknijCpu, Dock.Right); naglowekCpu.Children.Add(zamknijCpu);
        naglowekCpu.Children.Add(szczegolyCpuWartosc);
        var zawartoscCpu = new DockPanel();
        DockPanel.SetDock(naglowekCpu, Dock.Top); DockPanel.SetDock(wykresyRdzeni, Dock.Top);
        foreach (var (tytul, pole, szer) in new[] { ("PID", "Pid", 70.0), ("Nazwa", "Nazwa", 180.0), ("CPU %", "Cpu", 85.0), ("RAM MB", "Pamiec", 100.0), ("Ścieżka", "Sciezka", double.NaN) })
            szczegolyCpuProcesy.Columns.Add(new DataGridTextColumn { Header = tytul, SortMemberPath = pole, Binding = new System.Windows.Data.Binding(pole) { StringFormat = pole is "Cpu" or "Pamiec" ? "N1" : null }, Width = double.IsNaN(szer) ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(szer) });
        zawartoscCpu.Children.Add(naglowekCpu); zawartoscCpu.Children.Add(wykresyRdzeni);
        var stylNaglowka = new Style(typeof(DataGridColumnHeader));
        stylNaglowka.Setters.Add(new Setter(Control.BackgroundProperty, Kolory.Tlo0));
        stylNaglowka.Setters.Add(new Setter(Control.ForegroundProperty, Kolory.Tekst));
        stylNaglowka.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6)));
        szczegolyCpuProcesy.ColumnHeaderStyle = stylNaglowka;
        szczegolyCpuProcesy.Sorting += (_, e) =>
        {
            e.Handled = true;
            string pole = e.Column.SortMemberPath;
            kierunekProcesow = sortProcesow == pole && kierunekProcesow == System.ComponentModel.ListSortDirection.Ascending
                ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending;
            sortProcesow = pole;
            SortujProcesy();
        };
        int numerRdzenia = 0;
        foreach (var watki in RdzenieCpu.Watki())
        {
            var wykres = new Wykres(Kolory.Cpu, 61, 0) { Height = 45 };
            var etykieta = new TextBlock { Foreground = Kolory.Drugi, Text = $"Rdzeń {++numerRdzenia} · —" };
            var miniatura = new StackPanel { Width = 140, Margin = new Thickness(4) };
            miniatura.Children.Add(etykieta); miniatura.Children.Add(wykres); wykresyRdzeni.Children.Add(new Border { Child = miniatura, BorderBrush = Kolory.Ramka, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(3) });
            rdzenie.Add((watki, wykres, etykieta));
        }
        if (rdzenie.Count == 0 || !rdzeniePdh.Dostepny)
            wykresyRdzeni.Children.Add(new TextBlock { Text = "Dane rdzeni niedostępne", Foreground = Kolory.Szary });

        zawartoscCpu.Children.Add(szczegolyCpuProcesy); szczegolyCpu.Child = zawartoscCpu;
        cpu.Cursor = Cursors.Hand; cpu.ToolTip = "Kliknij, aby pokazać szczegóły CPU";
        cpu.MouseLeftButtonUp += (_, _) => szczegolyCpu.Visibility = szczegolyCpu.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        var stos = new DockPanel { ClipToBounds = true };
        foreach (var e in new UIElement[] { pasek, panelOpcji, siatka, dol }) { DockPanel.SetDock(e, Dock.Top); stos.Children.Add(e); }
        stos.Children.Add(szczegolyCpu); Content = stos;
        MaxHeight = SystemParameters.WorkArea.Height;
        Loaded += (_, _) => { if (WindowState == WindowState.Normal) { Height = Math.Min(ActualHeight, SystemParameters.WorkArea.Height); SizeToContent = SizeToContent.Manual; } else SizeToContent = SizeToContent.Manual; };
        SizeChanged += (_, _) => siatka.Columns = Math.Clamp((int)(ActualWidth / 220), 1, 4);
        StateChanged += (_, _) =>
        {
            // Zminimalizowane: odczyt co 5 s, GPU i czujniki stop. Z ikoną w zasobniku okno się chowa, a CPU/GPU/sieć zostają co 1 s.
            zegar.Interval = TimeSpan.FromSeconds(Zminimalizowane && !ust.Zasobnik ? 5 : 1);
            if (Zminimalizowane && ust.Zasobnik) Hide();
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int tak = 1; DwmSetWindowAttribute(hwnd, 20, ref tak, 4); // ciemny pasek tytułu
            HwndSource.FromHwnd(hwnd)!.AddHook(Hak);
            zasobnik = new Zasobnik(hwnd) { Stala = ust.Zasobnik };
        };
        Closing += (_, _) => ZapiszOkno();
        Closed += (_, _) => { zasobnik?.Dispose(); zdarzenia.Dispose(); };
        Application.Current.SessionEnding += (_, _) => ZapiszOkno();

        string? n = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("ProcessorNameString") as string;
        nazwaCpu = System.Text.RegularExpressions.Regex.Replace(n ?? "CPU", @"^(AMD|Intel\(R\) Core\(TM\))\s+|\s+\d+-Core|\s+Processor", "").Trim(); // „Ryzen 9 3900X”

        zdarzenia = new Zdarzenia(Dispatcher);
        zdarzenia.Zmiana += NoweZdarzenie;
        zdarzenia.Start();

        zegar.Tick += (_, _) => Odswiez();
        zegarSerwerow.Tick += (_, _) => OdswiezSerwery();
        probnik.Odczyt();
        OdswiezSerwery();
        zegar.Start(); zegarSerwerow.Start();
    }

    // ---------- kafelki ----------
    void Odswiez()
    {
        probnik.Odczyt();
        cpu.Ustaw($"{probnik.CpuProcent:0}%", nazwaCpu + cpuWaty, probnik.CpuProcent, Probnik.Top5(probnik.Cpu), v => $"{v:0.0} %");
        if (rdzeniePdh.Zbierz())
        {
            var wartosci = rdzeniePdh.Tablica(0).ToDictionary(x => x.Instancja, x => x.Wartosc);
            for (int i = 0; i < rdzenie.Count; i++)
            {
                var r = rdzenie[i];
                var znalezione = r.Watki.Select(w => wartosci.TryGetValue($"0,{w}", out var v) ? (double?)v : null).ToArray();
                if (znalezione.All(v => v.HasValue))
                {
                    double v = Math.Clamp(znalezione.Average(x => x!.Value), 0, 100);
                    r.Wykres.Dodaj(v); r.Etykieta.Text = $"Rdzeń {i + 1} · {v:0}%";
                }
            }
        }
        if (szczegolyCpu.Visibility == Visibility.Visible)
        {
            szczegolyCpuWartosc.Text = $"CPU · {nazwaCpu}{cpuWaty} · {probnik.CpuProcent:0}% · rdzenie fizyczne · ostatnie 60 s";
            szczegolyCpuProcesy.ItemsSource = probnik.Procesy.ToArray();
            SortujProcesy();
        }
        double gb = 1024.0 * 1024 * 1024, proc = 100 * probnik.RamUzyte / probnik.RamCalkowite;
        ram.Ustaw($"{probnik.RamUzyte / gb:0.0}/{probnik.RamCalkowite / gb:0} GB", $"{proc:0}% zajęte", proc, Probnik.Top5(probnik.Ram),
            v => v >= gb ? $"{v / gb:0.0} GB" : $"{v / (1024 * 1024):0} MB");
        Alert("ram", proc, ust.ProgRam, 3, true, "Mało wolnej pamięci RAM", $"RAM zajęty w {proc:0}% (próg {ust.ProgRam:0}%) od co najmniej 10 s");

        siecDyski.Odczyt();
        siec.Ustaw("↓ " + Bity(siecDyski.Pobieranie), "↑ " + Bity(siecDyski.Wysylanie), siecDyski.Pobieranie + siecDyski.Wysylanie,
            Probnik.Top5(probnik.Siec), v => Bity(8 * v));
        dyski.Ustaw(BajtyNaS(siecDyski.Dysk), zdrowieSz + siecDyski.Wolne, siecDyski.Dysk, Probnik.Top5(probnik.Dysk), BajtyNaS,
            zdrowieDymek + siecDyski.Wolne);
        foreach (var (litera, wolne) in siecDyski.WolneProc)
            Alert("wolne:" + litera, wolne, ust.ProgWolne, 2, false, "Mało miejsca na dysku", $"Dysk {litera}: wolne {wolne:0}% (próg {ust.ProgWolne:0}%)");

        zasobnik?.Takt();
        if (ust.Zasobnik && zasobnik != null)
        {
            double bps = siecDyski.Pobieranie + siecDyski.Wysylanie;
            // pasek sieci w skali log: 10 kb/s = pusty, 1 Gb/s = pełny (ruch skacze o rzędy wielkości)
            double siecProc = bps <= 1e4 ? 0 : Math.Clamp((Math.Log10(bps) - 4) / 5 * 100, 0, 100);
            zasobnik.Ustaw(probnik.CpuProcent, gpuProbnik.GpuProcent, siecProc,
                $"CPU {probnik.CpuProcent:0}% · GPU {gpuProbnik.GpuProcent:0}%\nRAM {proc:0}% · ↓ {Bity(siecDyski.Pobieranie)} ↑ {Bity(siecDyski.Wysylanie)}");
        }

        // GPU i czujniki co 2 s (drogie); zminimalizowane = stop, chyba że ikona w zasobniku pokazuje GPU (HANDOFF)
        Czujniki? c = czujniki.IsCompletedSuccessfully ? czujniki.Result : null;
        takt++;
        // zminimalizowane: czujniki tylko dla alertów, co ok. 10 s (zegar co 1 s z ikoną w zasobniku, co 5 s bez)
        if (Zminimalizowane && ust.Alerty && c != null && takt % (ust.Zasobnik ? 10 : 2) == 0) { c.Odczyt(); SprawdzCzujniki(c); }
        if (takt % 2 != 0 || Zminimalizowane && !ust.Zasobnik) return;
        if (!Zminimalizowane)
        {
            c?.Odczyt();
            cpuWaty = c?.CpuWaty is float cw ? $" · {cw:0} W" : "";
            gpuWaty = c?.GpuWaty is float gw ? $" · {gw:0} W" : "";
            if (c != null)
            {
                SprawdzCzujniki(c);
                var ssd = c.Dyski.Where(kv => kv.Value.Zycie != null).OrderBy(kv => kv.Value.Zycie).ToList();
                zdrowieSz = ssd.Count > 0 ? $"SSD min {ssd[0].Value.Zycie:0}% · " : "";
                zdrowieDymek = ssd.Count == 0 ? "" : "Stan SSD/NVMe (SMART, odczyt co 60 s; HDD pomijane):\n" + string.Join("\n", ssd.Select(kv =>
                    $"{kv.Key}: żywotność {kv.Value.Zycie:0}%" +
                    (kv.Value.Zapas is float za ? $", zapas komórek {za:0}%" + (kv.Value.ZapasProg is float zp ? $" (próg {zp:0}%)" : "") : "") +
                    (kv.Value.Godziny is float h ? $", {h:0} h pracy" : ""))) + "\n\n";
                temperatury.Ustaw(c.CpuTemp is float ct ? $"CPU {ct:0}°" : "—", c.GpuTemp is float gt ? $"GPU {gt:0}°" : "",
                    c.CpuTemp ?? 0, Probnik.Top5(c.Temperatury), v => $"{v:0} °C");
                int pracuje = c.Wentylatory.Values.Count(v => v > 0);
                double maks = c.Wentylatory.Count > 0 ? c.Wentylatory.Values.Max() : 0;
                wentylatory.Ustaw($"{maks:0} rpm", $"pracuje {pracuje} z {c.Wentylatory.Count}", maks, Probnik.Top5(c.Wentylatory), v => $"{v:0}");
            }
            else
            {
                string stan = czujniki.IsFaulted ? "czujniki niedostępne (LHM/PawnIO)" : "uruchamianie czujników…";
                temperatury.Ustaw("—", stan, 0, [], v => "");
                wentylatory.Ustaw("—", stan, 0, [], v => "");
            }
        }

        gpuProbnik.Odczyt(probnik.Nazwy);
        string nazwaGpu = (gpuProbnik.Dostepny ? gpuProbnik.Nazwa : "liczniki GPU niedostępne") + gpuWaty;
        gpu.Ustaw($"{gpuProbnik.GpuProcent:0}%", nazwaGpu, gpuProbnik.GpuProcent, Probnik.Top5(gpuProbnik.Gpu), v => $"{v:0.0} %");
        double vc = gpuProbnik.VramCalkowite, vproc = vc > 0 ? 100 * gpuProbnik.VramUzyte / vc : 0;
        vram.Ustaw(vc > 0 ? $"{gpuProbnik.VramUzyte / gb:0.0}/{vc / gb:0} GB" : $"{gpuProbnik.VramUzyte / gb:0.0} GB",
            vc > 0 ? $"{vproc:0}% zajęte" : "rozmiar nieznany", vproc, Probnik.Top5(gpuProbnik.Vram),
            v => v >= gb ? $"{v / gb:0.0} GB" : $"{v / (1024 * 1024):0} MB");
    }
    void SortujProcesy()
    {
        using (szczegolyCpuProcesy.Items.DeferRefresh())
        {
            szczegolyCpuProcesy.Items.SortDescriptions.Clear();
            szczegolyCpuProcesy.Items.SortDescriptions.Add(new(sortProcesow, kierunekProcesow));
            if (sortProcesow != "Pid") szczegolyCpuProcesy.Items.SortDescriptions.Add(new("Pid", System.ComponentModel.ListSortDirection.Ascending));
        }
        foreach (var kolumna in szczegolyCpuProcesy.Columns)
            kolumna.SortDirection = kolumna.SortMemberPath == sortProcesow ? kierunekProcesow : null;
    }
    string gpuWaty = "";

    // ---------- alerty progowe ----------
    void Alert(string klucz, double wartosc, double prog, double margines, bool wiecejZle, string tytul, string tekst)
    {
        if (ust.Alerty && alerty.Sprawdz(klucz, wartosc, prog, margines, wiecejZle, DateTime.Now)) zasobnik?.Powiadom(tytul, tekst, true);
    }

    void SprawdzCzujniki(Czujniki c)
    {
        if (c.CpuTemp is float ct) Alert("cpu", ct, ust.ProgCpu, 5, true, "Wysoka temperatura CPU", $"CPU {ct:0} °C (próg {ust.ProgCpu:0} °C) od co najmniej 10 s");
        if (c.GpuTemp is float gt) Alert("gpu", gt, ust.ProgGpu, 5, true, "Wysoka temperatura GPU", $"GPU {gt:0} °C (próg {ust.ProgGpu:0} °C) od co najmniej 10 s");
        foreach (var (n, z) in c.Dyski)
        {
            if (z.Zycie is float zy)
                Alert("zycie:" + n, zy, ust.ProgZycie, 0, false, "Dysk SSD kończy żywotność", $"{n}: pozostało {zy:0}% (próg {ust.ProgZycie:0}%). Zrób kopię danych.");
            // zapas poniżej progu producenta = NVMe sam zgłasza stan krytyczny; równy progowi bywa normą (KINGSTON SA1000: 100/100)
            if (z.Zapas is float za && z.ZapasProg is float zp)
                Alert("zapas:" + n, za, zp - 0.5, 0, false, "Dysk NVMe: kończy się zapas komórek", $"{n}: zapas {za:0}% poniżej progu producenta {zp:0}%. Zrób kopię danych.");
        }
    }

    // ---------- czas pracy ----------
    /// Po nieoczekiwanym wyłączeniu Windows zapisuje przy następnym starcie Kernel-Power 41 i/lub EventLog 6008.
    static string PrzyczynaStartu(DateTime start)
    {
        string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
            "*[System[((Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or (Provider[@Name='EventLog'] and EventID=6008))" +
            $" and TimeCreated[@SystemTime>='{Utc(start.AddMinutes(-2))}' and @SystemTime<='{Utc(start.AddMinutes(15))}']]]");
        using var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
        using var e = r.ReadEvent();
        return e == null ? "normalny" : $"po nieoczekiwanym wyłączeniu ({(e.Id == 41 ? "Kernel-Power 41" : "EventLog 6008")})";
    }

    void PokazCzasPracy()
    {
        var d = DateTime.Now - startSystemu;
        string ile = d.TotalDays >= 1 ? $"{(int)d.TotalDays} dni {d.Hours} h" : d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes} min" : $"{d.Minutes} min";
        string kiedy = startSystemu.ToString(startSystemu.Date == DateTime.Today ? "HH:mm" : "dd.MM HH:mm");
        czasPracy.Text = $"działa {ile} · start {kiedy}" + (przyczynaStartu.Length > 0 ? " · " + przyczynaStartu : "");
        czasPracy.Foreground = przyczynaStartu.StartsWith("po ") ? Kolory.Ostrzezenie : Kolory.Szary;
        czasPracy.ToolTip = "Czas od startu Windows. Przy szybkim uruchamianiu (Fast Startup) wyłączenie i włączenie to wybudzenie z hibernacji, więc start może być wcześniejszy niż ostatnie włączenie.\n" +
            "„po nieoczekiwanym wyłączeniu”: przy tym starcie Windows zapisał Kernel-Power 41 lub EventLog 6008 (zanik prądu, twardy reset, zawieszenie).";
    }

    static string Bity(double b) => b >= 1e9 ? $"{b / 1e9:0.0} Gb/s" : b >= 1e6 ? $"{b / 1e6:0.0} Mb/s" : $"{b / 1e3:0} kb/s";
    static string BajtyNaS(double b) => b >= 1 << 30 ? $"{b / (1 << 30):0.0} GB/s" : b >= 1 << 20 ? $"{b / (1 << 20):0.0} MB/s" : $"{b / 1024:0} KB/s";

    // ---------- serwery ----------
    void OdswiezSerwery()
    {
        if (++taktSerwerow % 12 == 0) { zdarzenia.Przytnij(); PokazZdarzenia(); } // co minutę
        PokazCzasPracy();
        serwery.Odczyt(probnik.Nazwy);
        var teraz = new Dictionary<string, string>();
        foreach (var s in serwery.Lista) foreach (var k in s.Klucze) teraz[k] = $"{s.Nazwa} (port {k[(k.LastIndexOf(':') + 1)..]})";
        if (poprzednieSerwery != null && ust.PowiadomieniaSerwery)
        {
            var zniknely = poprzednieSerwery.Where(kv => !teraz.ContainsKey(kv.Key)).Select(kv => kv.Value).Distinct().ToList();
            if (zniknely.Count > 0) zasobnik?.Powiadom(zniknely.Count == 1 ? "Serwer przestał nasłuchiwać" : $"{zniknely.Count} serwery przestały nasłuchiwać", string.Join("\n", zniknely), false);
        }
        poprzednieSerwery = teraz;

        string podpis = string.Join("|", serwery.Lista.Select(s => $"{s.Pid}{s.Nazwa}{s.PortyTekst}{s.WSieci}")) + serwery.Ukryte.Count;
        if (podpis == podpisSerwerow) return; // bez zmian = bez przebudowy wierszy
        podpisSerwerow = podpis;
        panelSerwerow.Wiersze.Children.Clear();
        foreach (var s in serwery.Lista.Take(WierszeListy - (serwery.Lista.Count > WierszeListy ? 1 : 0))) panelSerwerow.Wiersze.Children.Add(WierszSerwera(s));
        if (serwery.Lista.Count > WierszeListy)
        {
            var reszta = serwery.Lista.Skip(WierszeListy - 1).ToList();
            panelSerwerow.Wiersze.Children.Add(new TextBlock { Text = $"… i {reszta.Count} więcej", FontSize = 11, Foreground = Kolory.Szary,
                ToolTip = string.Join("\n", reszta.Select(s => $"{s.Nazwa}  {s.PortyTekst}")) });
        }
        panelSerwerow.Licznik.Text = serwery.Ukryte.Count > 0 ? $"+{serwery.Ukryte.Count} systemowych" : "";
        panelSerwerow.Licznik.ToolTip = serwery.Ukryte.Count > 0 ? string.Join("\n", serwery.Ukryte) : null;
    }

    FrameworkElement WierszSerwera(Serwer s)
    {
        var w = ListaPanel.Wiersz([16, double.NaN, 0, 0, 0],
            new Ellipse { Width = 7, Height = 7, Fill = s.WSieci ? Kolory.Ostrzezenie : Kolory.CpuP, HorizontalAlignment = HorizontalAlignment.Left },
            new TextBlock { Text = s.Nazwa, Foreground = Kolory.Tekst, TextTrimming = TextTrimming.CharacterEllipsis },
            new TextBlock { Text = s.WSieci ? "widoczny w sieci" : "tylko ten PC", FontSize = 11, Foreground = s.WSieci ? Kolory.Ostrzezenie : Kolory.Szary, Margin = new Thickness(8, 0, 8, 0) },
            new TextBlock { Text = s.PortyTekst, FontFamily = new FontFamily("Consolas"), Foreground = Kolory.Drugi },
            new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Foreground = Kolory.Szary, Margin = new Thickness(8, 0, 0, 0) });
        w.Cursor = Cursors.Hand;
        w.ToolTip = $"{s.Nazwa}\nPID {s.Pid} · {string.Join(", ", s.Porty.Select(p => $"{p.Adres}:{p.Port}"))}" +
            (s.WSieci ? "\nNasłuchuje nie tylko na 127.0.0.1 — inne urządzenia w sieci mogą się połączyć." : "") +
            (s.LiniaPolecen.Length > 0 ? "\n\n" + s.LiniaPolecen : "") + "\n\nKlik: podgląd w PowerShell (okno tylko czyta, nie zatrzymuje serwera)";
        w.MouseLeftButtonUp += (_, _) => Bezpiecznie(() => Serwery.Podglad(s));
        w.ContextMenu = Menu(
            ("Podgląd (PowerShell)", () => Serwery.Podglad(s)),
            ("Otwórz w przeglądarce", () => Serwery.OtworzBezAdmina(Serwery.Adres(s))),
            ("Otwórz folder", s.Folder == null ? null : () => Serwery.OtworzBezAdmina(s.Folder)),
            ("Kopiuj linię poleceń", s.LiniaPolecen.Length == 0 ? null : () => Clipboard.SetText(s.LiniaPolecen)));
        return w;
    }

    // ---------- zdarzenia ----------
    void NoweZdarzenie(Grupa? g, bool naZywo)
    {
        if (naZywo && g != null && g.Poziom <= 2 && ust.PowiadomieniaBledy && !ust.Wyciszone.Contains(g.Klucz))
        {
            // To samo źródło + ID w ciągu 15 min = jedno powiadomienie; następne po 15 min mówi, ile było w międzyczasie.
            if (g.Powiadomiono is DateTime kiedy && DateTime.Now - kiedy < TimeSpan.FromMinutes(15)) g.OdPowiadomienia++;
            else
            {
                string dopisek = g.OdPowiadomienia > 0 && g.Powiadomiono is DateTime od ? $"\nJeszcze {g.OdPowiadomienia}× od {od:HH:mm}" : "";
                zasobnik?.Powiadom(g.Poziom == 1 ? "Zdarzenie krytyczne" : "Błąd w Windows", $"{g.Tekst}\n{g.Zrodlo} {g.Id} · {g.Ile}× w 24 h{dopisek}", true);
                g.Powiadomiono = DateTime.Now; g.OdPowiadomienia = 0;
            }
        }
        PokazZdarzenia();
    }

    void PokazZdarzenia()
    {
        var lista = zdarzenia.Grupy.Values.Where(g => !ust.Wyciszone.Contains(g.Klucz) && (ust.Ostrzezenia || g.Poziom <= 2))
            .OrderByDescending(g => g.Ostatnio).Take(WierszeListy).ToList();
        panelZdarzen.Wiersze.Children.Clear();
        foreach (var g in lista)
        {
            var (znak, kolor) = g.Poziom switch { 1 => ("", Kolory.Blad), 2 => ("", Kolory.Blad), _ => ("", Kolory.Ostrzezenie) };
            var w = ListaPanel.Wiersz([18, 40, double.NaN, 0],
                new TextBlock { Text = znak, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Foreground = kolor },
                new TextBlock { Text = g.Ostatnio.ToString(g.Ostatnio.Date == DateTime.Today ? "HH:mm" : "ddd"), Foreground = Kolory.Drugi },
                new TextBlock { Text = g.Tekst, Foreground = Kolory.Tekst, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = g.Ile > 1 ? $"×{g.Ile}" : "", FontSize = 11, Foreground = Kolory.Drugi, Margin = new Thickness(6, 0, 0, 0) });
            w.ToolTip = g.Tekst;
            w.ToolTipOpening += (_, _) => w.ToolTip = Zdarzenia.PelnyOpis(g); // pełny opis dopiero przy najechaniu
            w.ContextMenu = Menu(
                ("Nie powiadamiaj o tym (wycisz)", () => { ust.Wyciszone.Add(g.Klucz); ust.Zapisz(); PokazZdarzenia(); }),
                ("Otwórz Podgląd zdarzeń", () => Process.Start(new ProcessStartInfo("eventvwr.msc", $"/c:{g.Log}") { UseShellExecute = true })),
                ("Kopiuj opis", () => Clipboard.SetText(Zdarzenia.PelnyOpis(g))));
            panelZdarzen.Wiersze.Children.Add(w);
        }
        if (lista.Count == 0)
            panelZdarzen.Wiersze.Children.Add(new TextBlock { Text = zdarzenia.Grupy.Count == 0 ? "wczytywanie dzienników…" : "brak błędów i ostrzeżeń", Foreground = Kolory.Szary, FontSize = 11 });

        int wyciszone = zdarzenia.Grupy.Values.Where(g => ust.Wyciszone.Contains(g.Klucz)).Sum(g => g.Ile);
        panelZdarzen.Licznik.Text = ust.Wyciszone.Count > 0 ? $"wyciszone {wyciszone}" : "";
        panelZdarzen.Licznik.Cursor = Cursors.Hand;
        panelZdarzen.Licznik.ToolTip = "Klik: przywróć wyciszone typy";
        panelZdarzen.Licznik.ContextMenu = Menu(ust.Wyciszone.Select(k => ($"Przywróć {k}", (Action?)(() => { ust.Wyciszone.Remove(k); ust.Zapisz(); PokazZdarzenia(); }))).ToArray());
        panelZdarzen.Licznik.MouseLeftButtonUp -= OtworzMenuLicznika;
        panelZdarzen.Licznik.MouseLeftButtonUp += OtworzMenuLicznika;
    }

    static void OtworzMenuLicznika(object s, MouseButtonEventArgs e) { if (((FrameworkElement)s).ContextMenu is { HasItems: true } m) { m.PlacementTarget = (UIElement)s; m.IsOpen = true; } }

    // ---------- opcje, okno, zasobnik ----------
    void BudujOpcje()
    {
        CheckBox Opcja(string tekst, bool stan, Action<bool> zmiana)
        {
            var c = new CheckBox { Content = tekst, IsChecked = stan, Foreground = Kolory.Drugi, FontSize = 12, Margin = new Thickness(0, 2, 16, 2) };
            c.Click += (_, _) => { zmiana(c.IsChecked == true); ust.Zapisz(); };
            return c;
        }
        panelOpcji.Children.Add(Opcja("Zawsze na wierzchu", ust.NaWierzchu, UstawNaWierzchu));
        panelOpcji.Children.Add(autostart = Opcja("Start z Windows (Harmonogram zadań, bez UAC)", false, UstawAutostart));
        autostart.IsEnabled = false;
        Task.Run(Autostart.Wlaczony).ContinueWith(t => Dispatcher.BeginInvoke(() => { autostart.IsChecked = t.IsCompletedSuccessfully && t.Result; autostart.IsEnabled = true; }));
        panelOpcji.Children.Add(Opcja("Ikona w zasobniku (minimalizacja chowa okno)", ust.Zasobnik, v =>
        {
            ust.Zasobnik = v;
            if (zasobnik != null) zasobnik.Stala = v;
            if (!v) zasobnik?.Ukryj();
        }));
        panelOpcji.Children.Add(Opcja("Powiadomienia: błędy i zdarzenia krytyczne", ust.PowiadomieniaBledy, v => ust.PowiadomieniaBledy = v));
        panelOpcji.Children.Add(Opcja("Powiadomienia: serwer przestał nasłuchiwać", ust.PowiadomieniaSerwery, v => ust.PowiadomieniaSerwery = v));
        panelOpcji.Children.Add(Opcja("Ostrzeżenia na liście zdarzeń", ust.Ostrzezenia, v => { ust.Ostrzezenia = v; PokazZdarzenia(); }));
        panelOpcji.Children.Add(Opcja("Powiadomienia: progi (10 s nad progiem; SSD < 10% żywotności)", ust.Alerty, v => ust.Alerty = v));
        panelOpcji.Children.Add(Prog("CPU °C", () => ust.ProgCpu, v => ust.ProgCpu = v, 40, 110));
        panelOpcji.Children.Add(Prog("GPU °C", () => ust.ProgGpu, v => ust.ProgGpu = v, 40, 110));
        panelOpcji.Children.Add(Prog("RAM %", () => ust.ProgRam, v => ust.ProgRam = v, 50, 99));
        panelOpcji.Children.Add(Prog("wolne miejsce %", () => ust.ProgWolne, v => ust.ProgWolne = v, 1, 50));
    }

    /// Pole progu: zapis po Enter albo wyjściu z pola; wartość spoza zakresu wraca do poprzedniej.
    FrameworkElement Prog(string tekst, Func<double> wartosc, Action<double> ustaw, double min, double max)
    {
        var pole = new TextBox { Text = wartosc().ToString("0"), Width = 34, Margin = new Thickness(4, 0, 0, 0), Background = Kolory.Tlo1, Foreground = Kolory.Tekst,
            BorderBrush = Kolory.Szary, CaretBrush = Kolory.Tekst, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = $"zakres {min:0}–{max:0}" };
        void Zatwierdz()
        {
            if (double.TryParse(pole.Text, out double v) && v >= min && v <= max) { ustaw(v); ust.Zapisz(); }
            pole.Text = wartosc().ToString("0");
        }
        pole.LostFocus += (_, _) => Zatwierdz();
        pole.KeyDown += (_, e) => { if (e.Key == Key.Enter) Zatwierdz(); };
        var w = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 16, 2) };
        w.Children.Add(new TextBlock { Text = tekst, Foreground = Kolory.Drugi, VerticalAlignment = VerticalAlignment.Center });
        w.Children.Add(pole);
        return w;
    }

    void UstawAutostart(bool wlacz)
    {
        autostart!.IsEnabled = false;
        Task.Run(() => Autostart.Ustaw(wlacz)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            autostart.IsEnabled = true;
            if ((t.IsFaulted ? t.Exception!.GetBaseException().Message : t.Result) is string blad)
            {
                autostart.IsChecked = !wlacz;
                MessageBox.Show(this, "Harmonogram zadań odmówił: " + blad, "sysMonBy3en4all", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }));
    }

    void UstawNaWierzchu(bool v)
    {
        ust.NaWierzchu = Topmost = v;
        pinezka.Foreground = v ? Kolory.Akcent : Kolory.Szary;
        pinezka.Text = v ? "" : ""; // przypięta / nieprzypięta
        if (panelOpcji.Children.Count > 0 && panelOpcji.Children[0] is CheckBox c) c.IsChecked = v;
        ust.Zapisz();
    }

    void ZapiszOkno()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (!r.IsEmpty) (ust.Lewo, ust.Gora, ust.Szerokosc) = (r.Left, r.Top, r.Width);
        ust.Zmaksymalizowane = WindowState == WindowState.Maximized;
        ust.Zapisz();
    }

    void Przywroc()
    {
        Show();
        if (Zminimalizowane) WindowState = ust.Zmaksymalizowane ? WindowState.Maximized : WindowState.Normal;
        Activate();
    }

    IntPtr Hak(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool obsluzone)
    {
        if (msg == Zasobnik.WM_ZASOBNIK && (int)lParam is Zasobnik.WM_LBUTTONUP or Zasobnik.WM_RBUTTONUP or Zasobnik.NIN_BALLOONUSERCLICK) Przywroc();
        else if (msg == Program.WM_POKAZ) Przywroc();
        else if (msg == Program.WM_TASKBARCREATED) zasobnik?.PoRestarcieEksploratora();
        return IntPtr.Zero;
    }

    static TextBlock Ikona(string znak, string podpowiedz, Action klik)
    {
        var t = new TextBlock { Text = znak, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Foreground = Kolory.Szary,
            Margin = new Thickness(10, 4, 2, 2), Cursor = Cursors.Hand, ToolTip = podpowiedz, Background = Brushes.Transparent };
        t.MouseLeftButtonUp += (_, _) => klik();
        return t;
    }

    ContextMenu Menu(params (string Tekst, Action? Akcja)[] pozycje)
    {
        var m = new ContextMenu();
        foreach (var (tekst, akcja) in pozycje)
        {
            var mi = new MenuItem { Header = tekst, IsEnabled = akcja != null };
            if (akcja != null) mi.Click += (_, _) => Bezpiecznie(akcja);
            m.Items.Add(mi);
        }
        return m;
    }

    void Bezpiecznie(Action a)
    {
        try { a(); }
        catch (Exception e) { MessageBox.Show(this, e.Message, "sysMonBy3en4all", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}

/// Panel listy (serwery, zdarzenia): tytuł, licznik po prawej, wiersze.
sealed class ListaPanel : Border
{
    public const double WysWiersza = 21;
    public readonly TextBlock Licznik = new() { FontSize = 11, Foreground = Kolory.Szary };
    public readonly StackPanel Wiersze = new();

    public ListaPanel(string tytul)
    {
        Background = Kolory.Tlo1; CornerRadius = new CornerRadius(6); Padding = new Thickness(8, 6, 8, 6); Margin = new Thickness(3);
        var naglowek = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        DockPanel.SetDock(Licznik, Dock.Right);
        naglowek.Children.Add(Licznik);
        naglowek.Children.Add(new TextBlock { Text = tytul, FontSize = 12, Foreground = Kolory.Drugi });
        var stos = new StackPanel();
        stos.Children.Add(naglowek); stos.Children.Add(Wiersze);
        Child = stos;
    }

    /// Wiersz w siatce; szerokość NaN = reszta miejsca, 0 = według treści.
    public static Grid Wiersz(double[] szerokosci, params FrameworkElement[] elementy)
    {
        var g = new Grid { Height = WysWiersza, Background = Brushes.Transparent }; // tło: klik działa na całym wierszu
        for (int i = 0; i < elementy.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(szerokosci[i]) ? new GridLength(1, GridUnitType.Star) : szerokosci[i] == 0 ? GridLength.Auto : new GridLength(szerokosci[i]) });
            elementy[i].VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(elementy[i], i);
            g.Children.Add(elementy[i]);
        }
        return g;
    }
}

/// Mapowanie rdzeni fizycznych na procesory logiczne według Windows.
static class RdzenieCpu
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetLogicalProcessorInformationEx(int relacja, IntPtr bufor, ref int rozmiar);
    public static List<int[]> Watki()
    {
        int rozmiar = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref rozmiar);
        var wynik = new List<int[]>();
        if (rozmiar <= 0) return wynik;
        IntPtr bufor = Marshal.AllocHGlobal(rozmiar);
        try
        {
            if (!GetLogicalProcessorInformationEx(0, bufor, ref rozmiar)) return wynik;
            for (int off = 0; off < rozmiar; )
            {
                IntPtr p = bufor + off;
                int dl = Marshal.ReadInt32(p, 4);
                if (dl < 48 || off + dl > rozmiar) return [];
                int grupy = (ushort)Marshal.ReadInt16(p, 30);
                if (grupy != 1 || Marshal.ReadInt16(p, 40) != 0) return [];
                ulong maska = unchecked((ulong)Marshal.ReadInt64(p, 32));
                var watki = Enumerable.Range(0, 64).Where(i => (maska & (1UL << i)) != 0).ToArray();
                if (watki.Length == 0) return [];
                wynik.Add(watki); off += dl;
            }
            return wynik;
        }
        finally { Marshal.FreeHGlobal(bufor); }
    }
}
