namespace SysMon;

/// Alerty progowe: powiadomienie, gdy wartość przekracza próg przez co najmniej 10 s.
/// Ponownie dopiero po powrocie za próg z marginesem (histereza) i nie częściej niż co 15 min dla tego samego klucza.
sealed class Alerty
{
    sealed class Stan { public DateTime? Od; public bool Zgloszony; public DateTime Ostatnio = DateTime.MinValue; }
    readonly Dictionary<string, Stan> stany = new();
    static readonly TimeSpan Trwanie = TimeSpan.FromSeconds(10), Odstep = TimeSpan.FromMinutes(15);

    /// Zwraca true, gdy trzeba teraz powiadomić. wiecejZle: true dla temperatury/RAM, false dla wolnego miejsca i żywotności.
    public bool Sprawdz(string klucz, double wartosc, double prog, double margines, bool wiecejZle, DateTime teraz)
    {
        if (!stany.TryGetValue(klucz, out var s)) stany[klucz] = s = new();
        bool przekroczony = wiecejZle ? wartosc >= prog : wartosc <= prog;
        if (!przekroczony)
        {
            s.Od = null;
            if (wiecejZle ? wartosc < prog - margines : wartosc > prog + margines) s.Zgloszony = false;
            return false;
        }
        s.Od ??= teraz;
        if (s.Zgloszony || teraz - s.Od < Trwanie || teraz - s.Ostatnio < Odstep) return false;
        s.Zgloszony = true; s.Ostatnio = teraz;
        return true;
    }

    /// Samotest logiki (przy starcie programu): null = dobrze.
    public static string? Samotest()
    {
        var a = new Alerty(); var t = new DateTime(2026, 1, 1);
        bool[] wynik =
        [
            !a.Sprawdz("t", 90, 85, 5, true, t),                       // przekroczenie dopiero zaczęte
            a.Sprawdz("t", 90, 85, 5, true, t.AddSeconds(10)),          // 10 s nad progiem → alert
            !a.Sprawdz("t", 91, 85, 5, true, t.AddSeconds(20)),         // już zgłoszony
            !a.Sprawdz("t", 82, 85, 5, true, t.AddSeconds(30)),         // poniżej progu, ale w marginesie: nadal zgłoszony
            !a.Sprawdz("t", 90, 85, 5, true, t.AddMinutes(20)),         // wrócił nad próg bez zejścia za margines → cisza
            !a.Sprawdz("t", 79, 85, 5, true, t.AddMinutes(21)),         // zejście za margines uzbraja alert
            !a.Sprawdz("t", 90, 85, 5, true, t.AddMinutes(22)),         // nowe przekroczenie, liczy 10 s od nowa
            a.Sprawdz("t", 90, 85, 5, true, t.AddMinutes(22).AddSeconds(10)),
            !a.Sprawdz("w", 15, 10, 2, false, t),                       // wolne 15% przy progu 10% → bez alertu
            !a.Sprawdz("w", 9, 10, 2, false, t),
            a.Sprawdz("w", 9, 10, 2, false, t.AddSeconds(10)),          // za mało wolnego przez 10 s → alert
        ];
        int zly = Array.IndexOf(wynik, false);
        return zly < 0 ? null : $"logika alertów, przypadek {zly + 1}";
    }
}
