# Wpis w Microsoft Store — teksty do wklejenia w Partner Center

Rynek: Polska (interfejs tylko po polsku). Język wpisu: polski. Kategoria: Narzędzia programistyczne → lub „Narzędzia i programy użytkowe” (Utilities & tools).

## Nazwa
sysMonBy3en4all

## Krótki opis (do 100 znaków; wolne pole „Short description”)
Monitor zasobów, lokalnych serwerów i zdarzeń Windows — wszystko na jednym ekranie.

## Opis
sysMonBy3en4all pokazuje stan komputera na jednym ekranie, bez przełączania kart.

• 8 kafelków: CPU, GPU, RAM, VRAM, sieć, dyski, temperatury i wentylatory — każdy z wykresem z ostatniej minuty i listą 5 największych odbiorców.
• Panel CPU: obciążenie każdego fizycznego rdzenia i tabela procesów (PID, ścieżka, CPU, RAM) z sortowaniem.
• Panel serwerów: porty nasłuchujące z nazwą programu z linii poleceń; serwery widoczne w sieci wyróżnione. Dla Ollamy załadowany model i zajęta pamięć VRAM.
• Zdarzenia Windows z 24 h z polskimi opisami najważniejszych błędów (nieoczekiwane wyłączenie, błędy dysku, sterownik grafiki, awarie programów).
• Alerty: temperatura CPU/GPU, zajęta pamięć, wolne miejsce na dysku, zużycie SSD/NVMe.
• Czas pracy i przyczyna ostatniego startu, ikona w zasobniku, start z Windows, tryb „zawsze na wierzchu”.

Lekki: około 0,3% CPU. Nie wysyła żadnych danych.

Temperatury, wentylatory, pobór mocy i SMART dysków wymagają uruchomienia jako administrator (przycisk w Opcjach) oraz sterownika PawnIO (https://pawnio.eu), instalowanego osobno.

Wersja beta.

## Funkcje (Product features, po jednej w wierszu)
Wykresy CPU, GPU, RAM, VRAM, sieci i dysków z top 5 procesów
Obciążenie fizycznych rdzeni i tabela procesów
Lokalne serwery i porty z nazwą programu
Zdarzenia Windows z polskimi opisami
Alerty temperatur, pamięci, miejsca i zużycia SSD
Bez telemetrii, bez połączeń z internetem

## Słowa kluczowe (do 7)
monitor systemu; menedżer zadań; temperatura CPU; GPU; porty; dziennik zdarzeń; Ollama

## Polityka prywatności (URL)
Do opublikowania: treść w pakiet/polityka-prywatnosci.md (np. jako strona GitHub Pages). URL wpisać w „Privacy policy URL”.

## Uwagi dla certyfikacji (Notes for certification)
Aplikacja startuje bez uprawnień administratora. Funkcja allowElevation jest używana wyłącznie, gdy użytkownik kliknie w Opcjach „Uruchom jako administrator (czujniki)”: odczyt temperatur, wentylatorów i poboru mocy przez sterownik PawnIO (instalowany osobno przez użytkownika, nie jest częścią pakietu) oraz SMART dysków wymaga uprawnień administratora. Bez tego wszystkie pozostałe funkcje działają. Aplikacja nie wysyła danych i nie łączy się z internetem (tylko localhost dla lokalnej Ollamy).

## Wymagania systemowe
Windows 10 2004 (19041) lub nowszy, x64.

## Grafiki
- Ikona: Assets/sklep-300.png (300×300) — jeśli Partner Center poprosi o logo do wpisu.
- Zrzuty ekranu (min. 1, 1366×768 lub więcej): DO ZROBIENIA — zrzut okna bez nazwy komputera i prywatnych ścieżek.
