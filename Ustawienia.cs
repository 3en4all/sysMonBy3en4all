using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.Json;

namespace SysMon;

/// Ustawienia w %LOCALAPPDATA%\sysMonBy3en4all\ustawienia.json. Zmieniane tylko przełącznikami w oknie (bez ręcznej edycji pliku).
sealed class Ustawienia
{
    public bool NaWierzchu { get; set; }
    public bool Zasobnik { get; set; }
    public bool PowiadomieniaBledy { get; set; } = true;
    public bool PowiadomieniaSerwery { get; set; } = true;
    public bool Ostrzezenia { get; set; } = true;
    public bool Alerty { get; set; } = true;
    public double ProgCpu { get; set; } = 90;   // °C
    public double ProgGpu { get; set; } = 85;   // °C
    public double ProgRam { get; set; } = 90;   // % zajęte
    public double ProgWolne { get; set; } = 10; // % wolnego miejsca na dysku
    public double ProgZycie { get; set; } = 10; // % pozostałej żywotności SSD (bez pola w oknie)
    public List<string> Wyciszone { get; set; } = ["DistributedCOM/10016", "DistributedCOM/10001", "DistributedCOM/10010"];
    public double? Lewo { get; set; }
    public double? Gora { get; set; }
    public double? Szerokosc { get; set; } // wysokość wynika z treści (SizeToContent)
    public bool Zmaksymalizowane { get; set; }

    static readonly string Plik = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sysMonBy3en4all", "ustawienia.json");

    public static Ustawienia Wczytaj()
    {
        try { return File.Exists(Plik) ? JsonSerializer.Deserialize<Ustawienia>(File.ReadAllText(Plik)) ?? new() : new(); }
        catch { return new(); } // uszkodzony plik = ustawienia domyślne; zapis przy zamknięciu go naprawi
    }

    public void Zapisz()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Plik)!);
            File.WriteAllText(Plik + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(Plik + ".tmp", Plik, true); // atomowo: przerwany zapis nie zostawi połowy pliku
        }
        catch { }
    }
}

/// Start z Windows. Pakiet MSIX (Store): StartupTask z manifestu pakietu, bez administratora.
/// Bez pakietu: Harmonogram zadań z najwyższymi uprawnieniami (tworzenie wymaga administratora).
static class Autostart
{
    const string Zadanie = "sysMonBy3en4all";

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetCurrentPackageFullName(ref int dlugosc, System.Text.StringBuilder? nazwa);
    public static readonly bool Pakiet = PakietSprawdz();
    static bool PakietSprawdz() { int d = 0; return GetCurrentPackageFullName(ref d, null) != 15700; } // 15700 = APPMODEL_ERROR_NO_PACKAGE

    static Windows.ApplicationModel.StartupTask ZadaniePakietu() => Windows.ApplicationModel.StartupTask.GetAsync("sysMonStart").AsTask().Result;

    public static bool Wlaczony() => Pakiet
        ? ZadaniePakietu().State is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy
        : Schtasks($"/Query /TN \"{Zadanie}\"").Kod == 0;

    /// Zwraca null przy sukcesie, inaczej komunikat błędu.
    public static string? Ustaw(bool wlacz)
    {
        if (!Pakiet) return UstawZadanie(wlacz);
        var z = ZadaniePakietu();
        if (!wlacz) { z.Disable(); return null; }
        var s = z.RequestEnableAsync().AsTask().Result;
        return s is Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy
            ? null : "Windows nie pozwolił (Ustawienia → Aplikacje → Uruchamianie): " + s;
    }

    static (int Kod, string Wyjscie) Schtasks(string argumenty)
    {
        var psi = new ProcessStartInfo("schtasks.exe", argumenty) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        string wyj = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, wyj.Trim());
    }

    static string? UstawZadanie(bool wlacz)
    {
        if (!wlacz) { var (k, w) = Schtasks($"/Delete /TN \"{Zadanie}\" /F"); return k == 0 ? null : w; }
        string uzytkownik = SecurityElement.Escape($@"{Environment.UserDomainName}\{Environment.UserName}");
        string exe = SecurityElement.Escape(Environment.ProcessPath!);
        // Bez limitu czasu (domyślnie 72 h ubija zadanie), bez zatrzymania na baterii, drugi start ignorowany.
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>sysMonBy3en4all: monitor zasobów, start po zalogowaniu</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{uzytkownik}</UserId><Delay>PT15S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{uzytkownik}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command></Exec></Actions>
            </Task>
            """;
        string plik = Path.Combine(Path.GetTempPath(), "sysMonBy3en4all-zadanie.xml");
        try
        {
            File.WriteAllText(plik, xml, System.Text.Encoding.Unicode);
            var (kod, wyj) = Schtasks($"/Create /TN \"{Zadanie}\" /XML \"{plik}\" /F");
            return kod == 0 ? null : wyj;
        }
        finally { File.Delete(plik); }
    }
}
