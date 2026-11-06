using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SysMon;

/// Kafelek: nazwa, wartość, linia szczegółu, wykres 60 s, top 5 z paskiem względnym.
sealed class Kafelek : Border
{
    const double SzerPaska = 30;
    readonly TextBlock wartosc = new() { FontSize = 14, Foreground = Kolory.Tekst };
    readonly TextBlock szczegol = new() { FontSize = 11, Foreground = Kolory.Szary, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Wykres wykres;
    readonly TextBlock[] nazwy = new TextBlock[5], wartosci = new TextBlock[5];
    readonly Border[] paski = new Border[5];

    public Kafelek(string nazwa, Color kolor, int punktyWykresu = 61, double skalaMin = 0)
    {
        Background = Kolory.Tlo1; CornerRadius = new CornerRadius(6); Padding = new Thickness(8, 6, 8, 6); Margin = new Thickness(3);
        var stos = new StackPanel();

        var naglowek = new DockPanel();
        DockPanel.SetDock(wartosc, Dock.Right);
        naglowek.Children.Add(wartosc);
        naglowek.Children.Add(new TextBlock { Text = nazwa, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Kolory.Tekst, VerticalAlignment = VerticalAlignment.Center });
        stos.Children.Add(naglowek);
        stos.Children.Add(szczegol);
        stos.Children.Add(wykres = new Wykres(kolor, punktyWykresu, skalaMin));

        var pedzel = Kolory.Zamroz(new SolidColorBrush(kolor));
        for (int i = 0; i < 5; i++)
        {
            var w = new Grid();
            w.ColumnDefinitions.Add(new ColumnDefinition());
            w.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SzerPaska + 12) });
            w.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            w.Children.Add(nazwy[i] = new TextBlock { FontSize = 12, Foreground = Kolory.Tekst, TextTrimming = TextTrimming.CharacterEllipsis });
            var tor = new Border { Height = 3, CornerRadius = new CornerRadius(2), Background = Kolory.Ramka, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = paski[i] = new Border { Height = 3, CornerRadius = new CornerRadius(2), Background = pedzel, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 } };
            Grid.SetColumn(tor, 1);
            w.Children.Add(tor);
            w.Children.Add(wartosci[i] = new TextBlock { FontSize = 12, Foreground = Kolory.Drugi, TextAlignment = TextAlignment.Right });
            Grid.SetColumn(wartosci[i], 2);
            stos.Children.Add(w);
        }
        Child = stos;
    }

    public void Ustaw(string w, string sz, double procentWykresu, List<(string Nazwa, double Wartosc)> top, Func<double, string> format, string? dymek = null)
    {
        wartosc.Text = w;
        szczegol.Text = sz;
        szczegol.ToolTip = dymek ?? sz; // pełny tekst, gdy linia się nie mieści (np. wolne miejsce na 8 dyskach)
        wykres.Dodaj(procentWykresu);
        double max = top.Count > 0 && top[0].Wartosc > 0 ? top[0].Wartosc : 1;
        for (int i = 0; i < 5; i++)
        {
            bool jest = i < top.Count;
            nazwy[i].Text = jest ? top[i].Nazwa : "";
            wartosci[i].Text = jest ? format(top[i].Wartosc) : "";
            paski[i].Width = jest ? Math.Max(0, SzerPaska * top[i].Wartosc / max) : 0;
        }
    }
}

/// Wykres 60 s, rysowany bezpośrednio (bez kontrolek na punkt). Skala 0–100%, albo autoskala (skalaMin > 0) do maksimum na wykresie.
sealed class Wykres : FrameworkElement
{
    readonly double[] dane;
    readonly double skalaMin;
    int ile; // ile punktów już zebrano; rysujemy tylko je, bez udawanej historii
    readonly Pen pioro;
    readonly Brush wypelnienie;

    public Wykres(Color kolor, int punkty, double skalaMin)
    {
        dane = new double[punkty];
        this.skalaMin = skalaMin;
        Height = 30; Margin = new Thickness(0, 3, 0, 4);
        pioro = new Pen(Kolory.Zamroz(new SolidColorBrush(kolor)), 1.2);
        pioro.Freeze();
        wypelnienie = Kolory.Zamroz(new SolidColorBrush(Color.FromArgb(38, kolor.R, kolor.G, kolor.B)));
    }

    public void Dodaj(double v)
    {
        Array.Copy(dane, 1, dane, 0, dane.Length - 1);
        dane[^1] = v;
        ile = Math.Min(ile + 1, dane.Length);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ile < 2) return;
        double w = ActualWidth, h = ActualHeight, krok = w / (dane.Length - 1);
        int od = dane.Length - ile;
        double skala = 100;
        if (skalaMin > 0)
        {
            skala = skalaMin;
            for (int i = od; i < dane.Length; i++) skala = Math.Max(skala, dane[i]);
        }
        Point P(int i) => new(i * krok, h - Math.Clamp(dane[i] / skala, 0, 1) * h);

        var obszar = new StreamGeometry();
        using (var c = obszar.Open())
        {
            c.BeginFigure(new Point(od * krok, h), true, true);
            for (int i = od; i < dane.Length; i++) c.LineTo(P(i), false, false);
            c.LineTo(new Point(w, h), false, false);
        }
        obszar.Freeze();
        dc.DrawGeometry(wypelnienie, null, obszar);

        var linia = new StreamGeometry();
        using (var c = linia.Open())
        {
            c.BeginFigure(P(od), false, false);
            for (int i = od + 1; i < dane.Length; i++) c.LineTo(P(i), true, true);
        }
        linia.Freeze();
        dc.DrawGeometry(null, pioro, linia);
    }
}
