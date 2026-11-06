using System.Runtime.InteropServices;

namespace SysMon;

/// Zapytanie PDH (liczniki wydajności) po angielskich nazwach — działa na polskim Windows.
sealed class Pdh
{
    [DllImport("pdh.dll")] static extern int PdhOpenQueryW(IntPtr zrodlo, IntPtr uzytk, out IntPtr zapytanie);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounterW(IntPtr zapytanie, string sciezka, IntPtr uzytk, out IntPtr licznik);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr zapytanie);
    [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr licznik, uint format, IntPtr typ, out Wartosc wartosc);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArrayW(IntPtr licznik, uint format, ref uint rozmiar, out uint ile, IntPtr bufor);

    [StructLayout(LayoutKind.Sequential)] struct Wartosc { public uint Status; public uint wyrownanie; public double Liczba; }

    const uint PDH_FMT_DOUBLE = 0x200;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    const int RozmiarElementu = 24; // PDH_FMT_COUNTERVALUE_ITEM_W na x64: wskaźnik nazwy, CStatus, wartość double

    readonly IntPtr zapytanie;
    readonly IntPtr[] liczniki;
    IntPtr bufor = IntPtr.Zero;
    uint dlugoscBufora;
    public readonly bool Dostepny;

    public Pdh(params string[] sciezki)
    {
        liczniki = new IntPtr[sciezki.Length];
        Dostepny = PdhOpenQueryW(IntPtr.Zero, IntPtr.Zero, out zapytanie) == 0;
        for (int i = 0; Dostepny && i < sciezki.Length; i++)
            Dostepny = PdhAddEnglishCounterW(zapytanie, sciezki[i], IntPtr.Zero, out liczniki[i]) == 0;
        if (Dostepny) PdhCollectQueryData(zapytanie); // liczniki „na sekundę” i % potrzebują próbki bazowej
    }

    public bool Zbierz() => Dostepny && PdhCollectQueryData(zapytanie) == 0;

    public double Jedna(int i) =>
        PdhGetFormattedCounterValue(liczniki[i], PDH_FMT_DOUBLE, IntPtr.Zero, out var w) == 0 && w.Status <= 1 ? w.Liczba : 0;

    /// Wszystkie instancje licznika z symbolem wieloznacznym (*).
    public List<(string Instancja, double Wartosc)> Tablica(int i)
    {
        var wynik = new List<(string, double)>();
        uint rozmiar = 0;
        if (PdhGetFormattedCounterArrayW(liczniki[i], PDH_FMT_DOUBLE, ref rozmiar, out _, IntPtr.Zero) != PDH_MORE_DATA) return wynik;
        if (rozmiar > dlugoscBufora)
        {
            Marshal.FreeHGlobal(bufor);
            dlugoscBufora = rozmiar * 2;
            bufor = Marshal.AllocHGlobal((int)dlugoscBufora);
        }
        rozmiar = dlugoscBufora;
        if (PdhGetFormattedCounterArrayW(liczniki[i], PDH_FMT_DOUBLE, ref rozmiar, out uint ile, bufor) != 0) return wynik;
        for (int k = 0; k < ile; k++)
        {
            IntPtr e = bufor + k * RozmiarElementu;
            if (Marshal.ReadInt32(e, 8) > 1) continue; // CStatus: 0 = dane poprawne, 1 = nowe dane; reszta = brak próbki
            wynik.Add((Marshal.PtrToStringUni(Marshal.ReadIntPtr(e, 0)) ?? "", BitConverter.Int64BitsToDouble(Marshal.ReadInt64(e, 16))));
        }
        return wynik;
    }
}
