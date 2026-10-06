# Plan działania

## Cel produktu

Dostarczyć agentowi narzędzia do przejścia od konkretnego briefu moda do reviewowalnego projektu i pakietu: rozpoznać istniejące zasoby, tworzyć lub edytować obsługiwane pliki, ustalać i weryfikować TGI, walidować zależności, budować pakiet, raportować konflikty i — po potwierdzeniu izolacji — sprawdzać zachowanie w grze. To jest cel projektu; pojedynczy codec ani walidator tuningu nie oznaczają jego ukończenia. Obsługa rodziny zasobów jest deklarowana jako gotowa dopiero po testach jej rzeczywistych wejść i wyników.

**Kryterium końcowe:** agent dostaje brief referencyjnego moda, tworzy jego pliki projektu i pakiet od czystego katalogu, `project validate` oraz `project build` weryfikują oczekiwane TGI i payloady, raport konfliktów rozpoznaje wskazane nadpisania, a instalacja w profilu tymczasowym potwierdza obserwowalny scenariusz w grze. Zachowanie w runtime jest osobnym wynikiem od walidacji strukturalnej.

## Stan

- [x] Zainicjować repozytorium Git i rozwiązanie .NET 10.
- [x] Ustalić wersjonowany kontrakt JSON dla CLI.
- [x] Dodać build/inspect/verify pakietów i zachowanie poprzedniego wyniku po błędzie.
- [x] Dodać idempotentne project init, walidację archiwum `.ts4script` i build do odizolowanego katalogu.
- [x] Dodać indeks gry, wyszukiwanie po typie zasobu i raportowanie wielu providerów.
- [x] Dodać STBL JSON import/export/build oraz tuning XML export/build/import.
- [x] Dodać przegląd schematów i round-trip SimData przez LlamaLogic.
- [x] Dodać DST PNG export/build/import z raportem różnic pikseli.
- [x] Dodać raport konfliktów mod-vs-game i package-vs-package.
- [x] Dodać referencje projektu z przypiętego TDESC i jawne asercje format 3 (`observed` / `bundled`), zachowując pełne TGI, widoczność providerów i ograniczenia skanu.
- [x] Sprawdzać spójność indeksów `<g>/<r x>` w XML CombinedTuning podczas validate/build/import i walidacji projektu.
- [x] Wyodrębniać wybrany Instance tuning z binarnego CombinedTuning, rozwijać wspólne referencje `<r x>` i wyznaczać standalone TGI; potwierdzone na bieżącym `SimulationDeltaBuild0` dla `Mood_Stressed`.
- [ ] Rozszerzyć częściową walidację semantyczną tuningów o wymagane pola i referencje tuning/STBL/SimData; poszerzyć tylko te edycje SimData i rodziny obrazów, dla których są zweryfikowane fixture'y.
- [ ] Potwierdzić kolejność ładowania pakietów oraz wykonać smoke test w izolowanym profilu gry.
- [x] Potwierdzić ręcznie w grze widoczny efekt referencyjnego moda STBL (zrzut ekranu użytkownika: „Nowa gra (S4MT TEST)”). To potwierdza działanie podmiany tekstu, ale nie izolację profilu ani kolejność ładowania.

- [x] Importować pełny lokalny zestaw 1.128.90.1030 oraz przypinać walidację wspieranych wartości tuningu do wersji i SHA-256 snapshotu.
- [x] Zbudować powtarzalny brief referencyjnego moda z czystego projektu: podmienić jeden polski wpis STBL, zachować resztę tabeli i przejść `project validate/build` oraz kontrolę konfliktu.
- [x] Zbudować przykładowy tuning mod z projektu: ustawić `mood_colors` dla myszy SteelSeries, wybrać zielone `portrait_frames`, zachować `mood_names` i przejść walidację, build oraz odczyt pakietu.
- [ ] Ręcznie potwierdzić widoczny efekt moda tuningu w grze; pakiet testowy jest zainstalowany w Mods, ale jego działanie w runtime nie zostało jeszcze zgłoszone.

**Stan obecny:** użytkownik potwierdził widoczny efekt referencyjnego moda STBL. Mood tuning zmienia styl portretu przez istniejące `portrait_frames`, a nazwę przez osobne `mood_names`; zielone ramki `Inspired`/`Imaginative` mogą być użyte bez zmiany nazw „Spięty”/„Bardzo spięty”. Zrzut „Zainspirowany” przedstawiał Sima w tym nastroju i nie rozstrzygał testu. Efekt tuningu w grze trzeba potwierdzić, gdy Sim faktycznie ma nastrój „Bardzo spięty”. Ekstrakcja binarnego CombinedTuning jest kosztowna pamięciowo, dlatego nie jest uruchamiana automatycznie w project validate/build. Raport zależności jest częściowy; izolowany profil oraz kolejność ładowania nadal nie są potwierdzone. Nieobsługiwane rodziny muszą pozostać jawnie niezweryfikowane.

## Etapy

### 1. Format pakietu — zamknięty

- [x] Round-trip DBPF z zasobem tekstowym i binarnym, dokładnymi TGI i payloadami.
- [x] Nieudany build nie nadpisuje poprzedniego pakietu.
- [x] Odrzucać kolizje wyjścia z wejściem, także przez dowiązania symboliczne i junctiony.
- [x] `verify` udostępnia tę samą kontrolę agentowi.
- [x] Ograniczenia kompresji są opisane: `CompressionMode.Auto` nie jest dowodem zgodności dowolnego payloadu z grą.
- [x] Składać osobne wyjścia typed codeców w jeden pakiet; domyślnie odrzucać kolizje TGI i przy jawnej polityce weryfikować zachowane payloady.

### 2. Projekt moda — podstawowy workflow zamknięty

- [x] `project init`, `project validate`, `project build`.
- [x] Źródła projektu są ograniczone do katalogu projektu z uwzględnieniem dowiązań symbolicznych i junctionów.
- [x] `.ts4script` jest sprawdzany jako ZIP z modułami Python i kopiowany bajt po bajcie.
- [x] Build jest fingerprintowany; powtórzenie używa zweryfikowanego wyniku.
- [x] `project validate` parsuje rozpoznane zasoby tuningowe zadeklarowane jako `utf8`, dekoduje STBL oraz PNGImage, DST, LRLE i translucent JPEG, sprawdza strukturę fixture-backed DXT5 RLE2/RLES, a SimData sprawdza przez decode/encode/read-back. Nieczytelne SimData zwraca ostrzeżenie; błędy obsługiwanych formatów zwracają TGI i ścieżkę.
- [x] Odrzucać deklarację `utf8` dla rozpoznanych binarnych typów STBL, SimData, PNGImage, DSTImage, LRLE i translucent JPEG; tuning jako `bytes` oraz typy bez walidatora pozostają nieinterpretowane.
- [x] Sprawdzać spójność indeksów `<g>/<r x>` dla XML CombinedTuning w CLI i `project validate`.
- [ ] Dodać pozostałe walidatory semantyczne formatów oraz rozstrzygnąć ich zależności.

### 3. Indeks gry — wyszukiwanie gotowe, kolejność niezweryfikowana

- [x] Indeksować wyłącznie klucze DBPF; payloady pozostają nietknięte.
- [x] Zapisać wersję, fingerprint, ścieżkę pakietu, warstwę i metadane pliku w cache JSONL/GZip.
- [x] Zachować wielu providerów dla jednego TGI i wyszukiwać po pełnym TGI albo type.
- [x] Rozpoznawać pliki `*DeltaBuild*.package` jako wskazówki warstwy patch.
- [x] Cache jest wersjonowany, idempotentny i poza katalogiem gry.
- [ ] Potwierdzić efektywnego zwycięzcę przez runtime TS4. `precedenceVerified` pozostaje `false`; raporty nie wybierają zwycięzcy.

### 4. Tuning XML — edycja XML gotowa, hash sprawdzany jako kandydat

- [x] Export, build nowego zasobu oraz import do istniejącego pakietu.
- [x] Weryfikować TGI, odczyt XML po zapisie, zestaw kluczy i niezmienność innych zasobów.
- [x] Blokować DTD i zbyt duży XML w poleceniu validate/import/build.
- [x] Wyliczać kandydaty FNV32/FNV64 i warianty high-bit; porównywać z root `s` i opcjonalnym TGI bez zgadywania reguły typu.
- [x] Walidować spójność wewnętrznych indeksów `<g>/<r x>` w CombinedTuning XML, także przed build/import oraz w `project validate`.
- [x] Importować i przeglądać `.tdesc` jako deterministyczny snapshot JSON przypięty do deklarowanej wersji gry i digestu. Import zapisuje strukturę pól, źródłowe hashe i pozycje w plikach; nie kopiuje surowych schematów.
- [x] Na podstawie pełnego lokalnego zestawu EA `.tdesc` mapować wspierane kształty i typy XML, z dokładnym pinem wersji i digestu; odrzucać znane błędy w `tuning validate/build/import` i `project validate/build`.
- [ ] Rozszerzyć semantykę o wymagane pola oraz rozwiązywanie referencji tuning/STBL i zależności SimData. Enumeracje i referencje, których nie da się bezpiecznie powiązać, pozostają ostrzeżeniami.

Indeksy `<g>/<r x>` są sprawdzane jako surowe ciągi, nie jako identyfikatory zasobów. Binarny CombinedTuning pozostaje nieinterpretowany w `project validate`: dekodowanie jednej realnej próbki przez LlamaLogic rozwinęło ją do 9,765,191 wierszy i osiągnęło 1.285 GB pamięci roboczej. Pole `<T>` może zawierać klucz STBL albo referencję TGI zależnie od typu tunable’a. Wiele typów tuningu wymaga SimData, ale nie wszystkie; walidacja zależności czeka na mapę typów pól.

### 5. STBL i lokalizacja — edycja tabel gotowa

- [x] JSON eksport/import/build z pełnym TGI oraz kluczami 32-bit.
- [x] Zachować Unicode, puste wartości i klucze; import traktuje `entries` jako pełny stan tabeli.
- [x] Weryfikować odczyt zwrotny na 147 wpisach z gry, dodatkowym Unicode i pustym wpisie.
- [x] Mapować najbardziej znaczący bajt `instance` na locale LlamaLogic i odrzucać locale JSON, które nie zgadza się z TGI.
- [x] `project validate` dekoduje STBL zasoby bajtowe i odczytuje wszystkie wpisy przed buildem.
- [ ] Walidować referencje STBL z tuningów.

### 6. SimData — inspekcja, round-trip i ograniczona edycja gotowe

- [x] Raportować tabelę, schema, liczbę wierszy/kolumn i typy kolumn.
- [x] Round-trip modelu na realnym zasobie `BuildBuy` z gry.
- [x] Edytować istniejącą komórkę w unikalnej tabeli i kolumnie dla BOOL, typów całkowitych oraz FLOAT; weryfikować pozostałe komórki skalarne, schemat, ważność referencji i zasoby pakietu.
- [x] Edytować istniejącą poprawną komórkę `STRING8` z wartością ASCII, odrzucać NUL i znaki spoza ASCII oraz weryfikować odczyt zmienionego stringu, kodowanie modelu, zachowanie schematu (z wyjątkiem długości surowej tabeli `CHAR8`) i ważność pozostałych referencji.
- [x] Edytować stałe wektory `FLOAT3` i `FLOAT4` z walidacją liczby składowych i odczytem zwrotnym na fixture gry.
- [x] `project validate` wykonuje modelowy round-trip SimData; zasoby, których LlamaLogic nie umie zweryfikować, oznacza ostrzeżeniem zamiast uznawać je za niepoprawne.
- [ ] Dodać edycję `FLOAT2` po znalezieniu i zweryfikowaniu fixture z gry.
- [ ] Dodać edycję `HASHEDSTRING8`, innych referencji, dynamicznego `VECTOR`, struktur i kształtu tabel; dla każdej rodziny osobno potwierdzić semantykę referencji.
- [ ] Poszerzyć pokrycie schematów: jeden realny zasób wywołał błąd dekodowania LlamaLogic dla kolumny `VECTOR`; w 3 225 pakietach gry nie znaleziono poprawnej referencji `HASHEDSTRING8`, komórki `FLOAT2` ani poprawnej wartości dynamicznego `VECTOR`.

### 7. Obrazy — DST, PNGImage, LRLE, RLE2 i RLES gotowe warunkowo; pozostałe rodziny otwarte

- [x] Export/build/import dla `DSTImage` PNG z weryfikacją wymiarów i statystyk różnic pikseli.
- [x] Raw PNG export/build/import dla `PNGImage`; weryfikować payload bajt po bajcie oraz piksele; `project validate` sprawdza strukturę i pełne dekodowanie zadeklarowanego payloadu.
- [x] Export/build/import dla typów oznaczonych przez LlamaLogic jako TS4 translucent JPEG; weryfikować wymiary i raportować różnice pikseli.
- [x] Ograniczyć rozmiar PNG i wymiary IHDR przed dekodowaniem przez ImageSharp; konwersja DST do PNG przez LlamaLogic następuje wcześniej.
- [x] `project validate` dekoduje PNGImage, DST i rozpoznane typy translucent JPEG zadeklarowane jako surowe bajty; waliduje wygenerowane PNG i raportuje błędny payload z TGI oraz ścieżką źródła.
- [x] LRLE v1/v2 PNG export, v2 build/import i walidacja wszystkich mipów z limitami; dwa realne zasoby gry oraz wygenerowany pakiet przechodzą odczyt, a read-back zachowuje dokładnie RGBA. Import zachowuje pozostałe zasoby. Weryfikacja dotyczy struktury pakietu i pikseli, nie uruchomienia gry.
- [x] Dodać read-only PNG export wybranego mip-level `RLE2Image` DXT5. Trzy realne fixture’y przechodzą; pierwszy zweryfikowano na wszystkich 12 mipach, a łącznie 14 eksportów ma identyczne RGBA względem niezależnego dekodera FFmpeg.
- [x] Dodać RLE2 DXT5 build/import z pełnym łańcuchem mipów. Generator BC3 przechodzi odczyt zwrotny, zachowuje zasoby obok, a wszystkie sześć mipów syntetycznego fixture’a i dwa 1×1 przypadki alfa zgadzają się z FFmpeg. Test gradientu ma maksymalny błąd kanału 11 i średni 2.3955078125. Wynik jest stratny i nie został jeszcze sprawdzony w grze.
- [x] Dodać wybrany-mip export RLES DXT5 do bundle z osobnymi `color.png` i `mask.png`. Trzy pinned fixture’y, 13 eksportów (wszystkie 11 mipów pierwszego i mip 0 dwóch pozostałych) zgadzają się bajtowo z niezależnym oracle’em FFmpeg dla koloru i oracle’em komend/strumienia maski.
- [x] Dodać RLES build/import z pełnym łańcuchem mipów oraz odczytem zwrotnym obu płaszczyzn. Jeden przypięty fixture 512×1024 przechodzi 11 mipów: kolor porównuje się bajtowo z niezależnym DDS/FFmpeg, maska ze strumieniem maski; import zachowuje sentinel i zasoby pakietu. Odrzucono błędne maski, bundle mip>0 i kolizje ścieżek. Enkoder koloru BC3 jest stratny; obraz w grze pozostaje niezweryfikowany.
- [ ] Dodać `PNGImage2` i pozostałe warianty po znalezieniu fixture’ów.
- [ ] Zweryfikować przez podgląd/Sims 4 Studio lub w grze kanały, alfę, mipmapy i zachowanie tekstury. Dotychczasowy DST round-trip zmienił piksele (mean channel delta 3.33, max 247), więc nie jest bezstratny.

### 8. Inne zasoby i konflikty — przecięcia TGI gotowe

- [x] Porównać package moda z indeksem gry, zachowując wszystkich providerów.
- [x] Porównać dwa pakiety i zwrócić wspólne TGI.
- [x] Eksportować i podmieniać dowolny payload bajt po bajcie; read-back weryfikuje TGI, niezmienność reszty pakietu i payload.
- [x] Łączyć wyjścia typed codeców z jawną polityką kolizji TGI.
- [x] Dodać wąski codec CASP v52: inspekcję i zmianę nazwy o stałej długości, z zachowaniem pozostałych bajtów i read-back pakietu.
- [ ] Rozszerzyć CASP na kolejne pola i wersje; dodać codecs GEOM, CLIP i innych konkretnych potrzeb.
- [ ] Dodać graf zależności tuningów/SimData/STBL i semantyczne raporty nadpisania.

### 9. Smoke test w grze — otwarty

- [x] Wskazać i sprawdzić izolację folderu użytkownika przez Windows junction. `scripts/verify-game-userdata-junction.ps1` potwierdza zapis do celu tymczasowego bez dotykania profilu gry; nie jest to jeszcze live smoke test.
- [ ] Zainstalować wyłącznie do profilu tymczasowego, uruchomić konkretny scenariusz i zebrać logi/zrzuty.
- [ ] Oddzielać walidację strukturalną od wyniku działania w runtime.

Nie uruchamiać gry do smoke testu, dopóki nie będzie potwierdzonego sposobu izolacji katalogu użytkownika. [Instrukcja EA](https://help.ea.com/en/articles/the-sims/the-sims-4/how-to-install-mods-the-sims-4/) opisuje folder użytkownika dla modów, ale nie podaje przełącznika jego zmiany; statyczny skan lokalnych plików wykonywalnych nie potwierdza też flagi `userDataPath` ani `dataPath`. To nie dowodzi, że takich flag nie ma, więc etap pozostaje otwarty.

## Skrypty weryfikacyjne

- `scripts/verify-package-roundtrip.ps1`
- `scripts/verify-package-merge.ps1`
- `scripts/verify-project-workflow.ps1`
- `scripts/verify-agent-mod-authoring.ps1`
- `scripts/verify-game-index.ps1`
- `scripts/verify-stbl-roundtrip.ps1`
- `scripts/verify-tuning-roundtrip.ps1`
- `scripts/verify-tuning-shared-index.ps1`
- `scripts/verify-simdata-roundtrip.ps1`
- `scripts/verify-simdata-set.ps1`
- `scripts/verify-image-roundtrip.ps1`
- `scripts/verify-pngimage-roundtrip.ps1`
- `scripts/verify-resource-raw.ps1`
- `scripts/verify-conflict-report.ps1`
- `scripts/verify-tdesc-snapshot.ps1`
- `scripts/verify-path-collisions.ps1`
- `scripts/verify-tjpeg-roundtrip.ps1`
- `scripts/verify-lrle-roundtrip.ps1`
- `scripts/verify-rle2-export.ps1`
- `scripts/verify-rle2-writer.ps1`
- `scripts/verify-rles-export.ps1`
- `scripts/verify-rles-writer.ps1`
- `scripts/verify-game-userdata-junction.ps1`
- `scripts/inspect-rle-fixtures.ps1`
- `scripts/verify-rle-structural-validation.ps1`

Skrypty zasobów używają fixture'ów z lokalnej instalacji Sims 4. Tworzą tylko tymczasowe pliki pod systemowym katalogiem Temp i usuwają wyłącznie własny katalog fixture.

## Reguła akceptacji nowego formatu

Obsługa formatu jest zakończona, gdy S4MT ma fixture z docelowej wersji TS4, potwierdzony klucz, poprawne kodowanie, round-trip bez niezgłoszonej utraty danych i walidację wynikowego pakietu. Dla formatów wizualnych wymagany jest dodatkowo podgląd lub smoke test w grze.
