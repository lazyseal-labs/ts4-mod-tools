# Zielony nastrój „Stressed”

Ten przykład podmienia strojenie `Mood_Stressed`. Zmienia kolory `mood_colors` dla myszy SteelSeries i ustawia ramki portretu na zielone style `Inspired` i `Imaginative`. Oryginalne `mood_names` pozostają bez zmian, więc gra nadal wyświetla nazwę „Spięty” lub „Bardzo spięty”. Pakiet zawiera jeden zasób Tuning o TGI `0x03B33DDF:0x00000000:0xB17201C249771AF4`.

## Sprawdź kopiowanie pakietu bez gry

Z katalogu repozytorium uruchom:

```powershell
pwsh -NoProfile -File scripts/verify-runtime-package-staging.ps1
```

Weryfikator buduje ten przypięty przykład przez CLI w nowym katalogu tymczasowym. Tworzy tam syntetyczny profil z `Options.ini` i `Mods/Resource.cfg`, wywołuje tryb `-StageOnly`, a potem niezależnie porównuje hash i długość pakietu oraz obu plików profilu z ich źródłami. Tryb usuwa profil stagingowy i nie przełącza profilu użytkownika.

Ta kontrola sprawdza kopiowanie plików i bajty pakietu. Nie uruchamia The Sims 4, nie sprawdza kolejności ładowania modów ani wyglądu nastroju w grze. Zachowanie w grze wymaga ręcznej obserwacji na Windows.

## Sprawdź nastrój w grze na Windows

1. Zamknij The Sims 4.
2. Z katalogu repozytorium zbuduj projekt w nowym katalogu tymczasowym:

   ```powershell
   dotnet build S4MT.sln
   $runtimeRoot = Join-Path ([IO.Path]::GetTempPath()) "s4mt-agent-authoring-$([guid]::NewGuid().ToString('N'))"
   New-Item -ItemType Directory -Path $runtimeRoot | Out-Null
   $build = dotnet run --no-build --project src/S4MT.Cli -- project build --project examples/StressedMoodGreen --output-root (Join-Path $runtimeRoot 'built') | ConvertFrom-Json
   ```

3. Przygotuj profil izolowany:

   ```powershell
   pwsh -NoProfile -File scripts/prepare-runtime-profile.ps1 -Package $build.package
   ```

4. Uruchom grę ręcznie. Doprowadź Sima do stanu „Bardzo spięty”. Sprawdź, czy zielona ramka pojawia się obok oryginalnej nazwy nastroju.
5. Zamknij grę i przywróć oryginalny profil:

   ```powershell
   pwsh -NoProfile -File scripts/restore-runtime-profile.ps1
   ```

Skrypt przygotowujący zapisuje stan potrzebny do przywrócenia profilu. Skrypt przywracający usuwa też tymczasowy profil i katalog kompilacji.

## Odtwórz projekt

W katalogu repozytorium uruchom:

```powershell
dotnet build S4MT.sln
dotnet run --no-build --project src/S4MT.Cli -- project validate --project examples/StressedMoodGreen
```

Aby ponownie wyodrębnić bieżące strojenie z gry, użyj:

```powershell
dotnet run --no-build --project src/S4MT.Cli -- tuning extract --package 'C:\Program Files\EA Games\The Sims 4\Data\Simulation\SimulationDeltaBuild0.package' --combined-tgi 0x62E94D38:0x00000000:0xF1352E740C4710BA --name Mood_Stressed --output .\Mood_Stressed.xml
```

Walidacja używa przypiętego, ograniczonego snapshotu Mood dla builda 1.128.90.1030. Pokrycie walidatora pozostaje częściowe i jest raportowane w wynikach S4MT.
