using System.IO;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace SysMon;

/// Czujniki sprzętowe przez LibreHardwareMonitorLib (sterownik PawnIO, już zainstalowany na 3EN035).
/// Pamięć RAM (SPD) celowo wyłączona: jej biblioteka ma ścieżkę do sterownika WinRing0.
sealed class Czujniki
{
    readonly Computer komputer = new()
    {
        IsCpuEnabled = true, IsGpuEnabled = true, IsMotherboardEnabled = true, IsStorageEnabled = true, IsControllerEnabled = true,
        IsMemoryEnabled = false, IsNetworkEnabled = false, IsBatteryEnabled = false, IsPsuEnabled = false,
    };

    public static bool Admin => new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public Czujniki()
    {
        // komunikaty trafiają do kafelków Temperatury/Wentylatory
        if (!Admin) throw new InvalidOperationException("czujniki: uruchom jako administrator (Opcje)");
        if (Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO") == null)
            throw new InvalidOperationException("czujniki: brak sterownika PawnIO (pawnio.eu)");
        komputer.Open();
    }

    public void Odswiez()
    {
        foreach (var h in komputer.Hardware) Odswiez(h);
    }

    int takt;
    public float? CpuTemp, CpuWaty, GpuTemp, GpuWaty;
    public readonly Dictionary<string, double> Temperatury = new(), Wentylatory = new(); // °C / rpm
    /// Zdrowie SSD/NVMe z SMART według LHM: żywotność %, zapas komórek % i jego próg (NVMe), godziny pracy.
    /// LHM nie podaje jako czujników przeniesionych sektorów ani błędów odczytu.
    public sealed class Zdrowie { public float? Zycie, Zapas, ZapasProg, Godziny; }
    public readonly Dictionary<string, Zdrowie> Dyski = new();

    /// Odczyt co 2 s. Dyski SSD/NVMe co 60 s; HDD wcale — zapytanie SMART mogłoby budzić uśpione talerze.
    public void Odczyt()
    {
        bool dyski = takt++ % 30 == 0;
        foreach (var h in komputer.Hardware)
            if (h.HardwareType != HardwareType.Storage || dyski && !h.Identifier.ToString().StartsWith("/hdd/")) Odswiez(h);

        Temperatury.Clear(); Wentylatory.Clear(); Dyski.Clear();
        CpuTemp = CpuWaty = GpuTemp = GpuWaty = null;
        foreach (var (h, s) in Wszystkie())
        {
            if (s.Value is not float v) continue;
            if (h.HardwareType == HardwareType.Storage && s.SensorType is SensorType.Level or SensorType.Factor)
            {
                string n = h.Name.Trim();
                Zdrowie Z() => Dyski.TryGetValue(n, out var z) ? z : Dyski[n] = new();
                switch (s.Name)
                {
                    case "Life": Z().Zycie = v; break;
                    case "Available Spare": Z().Zapas = v; break;
                    case "Available Spare Threshold": Z().ZapasProg = v; break;
                    case "Power On Hours": Z().Godziny = v; break;
                }
                continue;
            }
            bool cpu = h.HardwareType == HardwareType.Cpu;
            bool gpu = h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;
            switch (s.SensorType)
            {
                // Super I/O płyty pominięte: bez profilu płyty LHM daje same numery, a dwa wejścia na 3EN035 pokazują 83–94 °C (niepodłączone)
                case SensorType.Temperature when cpu && s.Name is "Core (Tctl/Tdie)" or "CPU Package":
                    CpuTemp = v; Temperatury["CPU"] = v; break;
                case SensorType.Temperature when gpu && s.Name == "GPU Core":
                    GpuTemp = v; Temperatury["GPU"] = v; break;
                case SensorType.Temperature when gpu && s.Name == "GPU Hot Spot":
                    Temperatury["GPU hotspot"] = v; break;
                case SensorType.Temperature when h.HardwareType == HardwareType.Storage && s.Name is "Composite Temperature" or "Temperature":
                    Temperatury[h.Name.Trim()] = v; break;
                case SensorType.Fan when gpu:
                    Wentylatory[s.Name.Replace("GPU Fan", "GPU")] = v; break;
                case SensorType.Fan when v > 0: // gniazda płyty bez wentylatora zawsze dają 0
                    Wentylatory[(h.HardwareType == HardwareType.SuperIO ? "Płyta " : h.Name + " ") + s.Name.Replace("Fan ", "")] = v; break;
                case SensorType.Power when cpu && s.Name is "Package" or "CPU Package":
                    CpuWaty = v; break;
                case SensorType.Power when gpu && s.Name == "GPU Package":
                    GpuWaty = v; break;
            }
        }
    }

    static void Odswiez(IHardware h)
    {
        h.Update();
        foreach (var p in h.SubHardware) Odswiez(p);
    }

    public IEnumerable<(IHardware Sprzet, ISensor Czujnik)> Wszystkie()
    {
        foreach (var h in komputer.Hardware)
            foreach (var x in Z(h)) yield return x;

        static IEnumerable<(IHardware, ISensor)> Z(IHardware h)
        {
            foreach (var s in h.Sensors) yield return (h, s);
            foreach (var p in h.SubHardware)
                foreach (var x in Z(p)) yield return x;
        }
    }

    /// Tryb diagnostyczny `--czujniki <plik>`: zrzut wszystkich czujników do pliku tekstowego.
    public static void Zrzut(string plik)
    {
        var c = new Czujniki();
        c.Odswiez();
        Thread.Sleep(1500);
        c.Odswiez();
        var sb = new StringBuilder();
        foreach (var (h, s) in c.Wszystkie())
            sb.AppendLine($"{h.HardwareType} | {h.Name} | {s.SensorType} | {s.Name} | {s.Value?.ToString("0.0") ?? "-"} | {s.Identifier}");
        File.WriteAllText(plik, sb.ToString());
        c.komputer.Close();
    }
}
