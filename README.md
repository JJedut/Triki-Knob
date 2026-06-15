# Triki-Knob

Aplikacja Windows do laczenia sie z kontrolerem Triki przez Bluetooth Low Energy. Program odbiera dane z czujnikow ruchu i pozwala sterowac glosnoscia oraz przewijaniem na podstawie obrotu lub pochylenia urzadzenia.

## Podstawa projektu

Aplikacja powstala na podstawie projektu [AND-Y0/TrikiReader](https://github.com/AND-Y0/TrikiReader).

## Wymagania

- Windows 10 lub nowszy
- Bluetooth Low Energy
- Kontroler Triki
- .NET 9

## Uruchamianie z kodu

W folderze projektu:

```powershell
dotnet run
```

## Budowanie aplikacji

Aby zbudowac wersje Release:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Gotowy plik `.exe` znajdziesz w folderze `publish`.

## Ustawienia

Aplikacja zapamietuje ustawienia automatycznie, miedzy innymi:

- kierunek sterowania glosnoscia
- czulosc glosnosci
- martwa strefe glosnosci
- krok glosnosci
- kierunek przewijania
- czulosc przewijania
- martwa strefe przewijania
- predkosc przewijania

Ustawienia sa zapisane w:

```text
%AppData%\Triki-Knob\settings.json
```

## Ikona w zasobniku

Po zamknieciu lub zminimalizowaniu aplikacja moze dzialac w tle. Ikona w zasobniku systemowym pokazuje stan programu:

- zolta: normalny stan
- zielona: urzadzenie polaczone
- czerwona: blad lub problem z polaczeniem

## Jak dziala sterowanie

Kontroler Triki wysyla dane z czujnikow ruchu. Aplikacja odczytuje te dane i przelicza je na orientacje urzadzenia.

Glosnosc moze byc sterowana obrotem w lewo lub w prawo. Przewijanie moze byc sterowane pochyleniem urzadzenia. Im wiekszy stopien pochylenia, tym szybsze przewijanie.


Reszta moze byc rozwijana dalej wedlug potrzeb.
