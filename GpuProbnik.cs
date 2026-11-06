using Microsoft.Win32;

namespace SysMon;

/// Odczyt GPU i VRAM z liczników „GPU Engine” / „GPU Process Memory” / „GPU Adapter Memory” (te same co Menedżer zadań).
/// Jedno zapytanie PDH z symbolami wieloznacznymi: wszystkie instancje w jednym odczycie.
sealed class GpuProbnik
{
    readonly Pdh pdh = new(@"\GPU Engine(*)\Utilization Percentage", @"\GPU Process Memory(*)\Dedicated Usage", @"\GPU Adapter Memory(*)\Dedicated Usage");
    const int Silniki = 0, PamiecProcesow = 1, PamiecAdaptera = 2;

    public readonly string Nazwa = "GPU";
    public readonly double VramCalkowite; // bajty, z rejestru sterownika; 0 = nieznane
    public double GpuProcent, VramUzyte;
    public readonly Dictionary<string, double> Gpu = new(), Vram = new(); // % / bajty pamięci dedykowanej
    public bool Dostepny => pdh.Dostepny;

    public GpuProbnik()
    {
        // Karta z największą pamięcią w klasie „Display” (pomija wirtualne adaptery)
        using var klasa = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        foreach (string pod in klasa?.GetSubKeyNames() ?? [])
        {
            try
            {
                using var k = klasa!.OpenSubKey(pod);
                if (k?.GetValue("HardwareInformation.qwMemorySize") is long pamiec && pamiec > VramCalkowite)
                    (VramCalkowite, Nazwa) = (pamiec, (k.GetValue("DriverDesc") as string ?? "GPU").Replace("NVIDIA GeForce ", "").Replace("AMD Radeon ", "Radeon "));
            }
            catch (System.Security.SecurityException) { } // podklucz „Properties” bywa zablokowany
        }
    }

    public void Odczyt(Dictionary<long, string> nazwy)
    {
        if (!pdh.Zbierz()) return;
        string Nazwa(long pid) => nazwy.TryGetValue(pid, out var n) ? n : "pid " + pid;

        // GPU całości = najbardziej zajęty silnik (suma procesów na silnik); proces = jego najbardziej zajęty silnik — jak Menedżer zadań
        var silnik = new Dictionary<string, double>();
        var procesMax = new Dictionary<long, double>();
        foreach (var (inst, v) in pdh.Tablica(Silniki))
        {
            int l = inst.IndexOf("_luid_"), t = inst.IndexOf("_engtype_");
            if (l < 0 || t < l || Pid(inst) is not long pid) continue;
            string klucz = inst[l..t];
            silnik[klucz] = silnik.GetValueOrDefault(klucz) + v;
            procesMax[pid] = Math.Max(procesMax.GetValueOrDefault(pid), v);
        }
        GpuProcent = Math.Clamp(silnik.Count > 0 ? silnik.Values.Max() : 0, 0, 100);

        Gpu.Clear(); Vram.Clear();
        foreach (var (pid, v) in procesMax) Probnik.Dodaj(Gpu, Nazwa(pid), v);
        foreach (var (inst, v) in pdh.Tablica(PamiecProcesow))
            if (Pid(inst) is long pid) Probnik.Dodaj(Vram, Nazwa(pid), v);
        var adaptery = pdh.Tablica(PamiecAdaptera);
        VramUzyte = adaptery.Count > 0 ? adaptery.Max(a => a.Wartosc) : 0;
    }

    /// „pid_1234_luid_…” → 1234
    public static long? Pid(string instancja)
    {
        if (!instancja.StartsWith("pid_")) return null;
        int k = instancja.IndexOf('_', 4);
        return long.TryParse(k < 0 ? instancja[4..] : instancja[4..k], out long pid) ? pid : null;
    }

    public static string? Samotest() =>
        Pid("pid_1234_luid_0x00000000_0x0000D1B5_phys_0_eng_3_engtype_3D") == 1234 && Pid("pid_77") == 77 && Pid("luid_0x0_phys_0") is null
            ? null : "zły odczyt PID z nazwy instancji GPU";
}
