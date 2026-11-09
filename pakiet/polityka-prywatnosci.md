# sysMonBy3en4all — polityka prywatności

Obowiązuje od: 2026-11-09. Autor: Jacek Grodnicki (3en4all).

sysMonBy3en4all działa wyłącznie na Twoim komputerze. **Nie wysyła żadnych danych** do autora ani do innych podmiotów, nie zawiera telemetrii, reklam ani analityki.

## Co program odczytuje (lokalnie)
- obciążenie CPU, GPU, pamięci, dysków i sieci oraz listę procesów (nazwa, PID, ścieżka, zużycie zasobów),
- porty nasłuchujące i linie poleceń procesów, które je otworzyły (panel serwerów),
- dzienniki zdarzeń Windows System i Application z ostatnich 24 h,
- po uruchomieniu jako administrator i z zainstalowanym sterownikiem PawnIO: temperatury, wentylatory, pobór mocy i stan SMART dysków.

Dane są pokazywane w oknie i trzymane tylko w pamięci (historia 60 s). Nic z tego nie jest zapisywane na dysk.

## Połączenia sieciowe
Program łączy się tylko z adresami na Twoim komputerze (localhost), aby odczytać stan lokalnej Ollamy (`/api/ps`). Nie łączy się z internetem.

## Co program zapisuje
Wyłącznie ustawienia okna i opcji (pozycja, przełączniki, progi alertów, wyciszone zdarzenia) w pliku `ustawienia.json` w folderze danych aplikacji. Odinstalowanie programu usuwa ten plik.

## Kontakt
Zgłoszenia: https://github.com/3en4all/sysMonBy3en4all/issues
