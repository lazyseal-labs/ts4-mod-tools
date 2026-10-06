# Decyzja architektoniczna: .NET CLI z LlamaLogic

## Decyzja

S4MT jest bezstanowym CLI w .NET 10. LlamaLogic.Packages 3.8.2 odpowiada za DBPF i modelowe formaty zasobów. Własny model domeny przechowuje pełne TGI jako `ResourceId(type: uint, group: uint, instance: ulong)` i nie zależy od typów biblioteki poza adapterami.

```text
manifest / projekt / istniejący package
    -> walidacja CLI
    -> ResourceId + payload albo model typu zasobu
    -> adapter formatu
    -> DBPF tymczasowy
    -> ponowne otwarcie i weryfikacja
    -> atomowa podmiana wyniku
```

Kontrakt CLI zwraca JSON z `schemaVersion`. Budowa pakietu porównuje po odczycie dokładne klucze i bajty. Operacje oparte na modelu porównują model po ponownym otwarciu, a importy sprawdzają, że zasoby poza edytowanym TGI pozostały niezmienione.

Kodeki mają własne granice: `StblWork`, `TuningWork`, `SimDataWork` oraz `ImageWork`. `GameCatalogWork` indeksuje klucze bez odczytywania payloadów, a `ConflictWork` raportuje przecięcia TGI. Nie wyliczamy brakujących kluczy z nazw ani nie zgadujemy skutecznego nadpisania.

`ManifestReader` zamienia deklarowane kodowanie na `ResourceEncoding` i zachowuje rozwiązaną ścieżkę pliku źródłowego. `ModProjectWork.ValidateAsync` przekazuje do `TuningWork` rozpoznane zasoby tuningowe zadeklarowane jako UTF-8, zasoby `StringTable` do `StblWork`, a PNGImage bezpośrednio do ImageSharp. Dla DST i rozpoznanych typów TS4 translucent JPEG `ImageWork` wkłada payload do tymczasowego modelu pakietu w pamięci, dekoduje PNG przez LlamaLogic i sprawdza piksele przez ImageSharp. LRLE sprawdza własnym, niezależnym parserem wszystkich mipów, bez tymczasowego pakietu. SimData przechodzi LlamaLogic `DataModel`. Błędy XML, STBL i obsługiwanych obrazów wracają jako błędy z TGI i ścieżką. Jeżeli LlamaLogic nie potrafi dekodować lub stabilnie zakodować SimData, projekt dostaje ostrzeżenie, a payload pozostaje dostępny jako surowe bajty. Pozostałe zasoby `bytes` pozostają nieinterpretowane.

Projekt zachowuje pliki `.ts4script` bajt po bajcie. Build trafia do katalogu nazwanego fingerprintem zawartości, jest weryfikowany i nigdy nie instaluje moda do gry.

## Dlaczego ten wariant

LlamaLogic już obsługuje DBPF oraz modele STBL, XML i SimData. Pozwala to wspólnie używać jednej implementacji pakietów, a ograniczenia konkretnego formatu pokazywać jako diagnostykę. ImageSharp obsługuje PNG, rastry LRLE oraz porównanie pikseli po konwersjach obrazów.

Python pozostaje runtime'em skryptów TS4. Narzędzia autorskie mogą działać w .NET bez zmieniania języka moda.

## Granice i ryzyka

- `CompressionMode.Auto` nie stanowi dowodu poprawności każdego payloadu dla silnika gry. Każda rodzina wymaga własnego fixture'u i odczytu zwrotnego.
- Tuning XML przechodzi kontrolę składni oraz bezpieczne zapisywanie. `project validate` stosuje tę kontrolę do rozpoznanych zasobów deklarowanych jako UTF-8. Referencje STBL i zasobów wymagają typu tunable’a: element `<T>` może przechowywać klucz tekstu albo TGI zasobu, zależnie od pola ([Tuning XML](https://simsedit.com/tuning-mods/tuning-xml/)). SimData jest wymagane przez wiele typów tuningów, ale nie wszystkie ([Modders Reference](https://thesims4moddersreference.org/reference/file-types/)). Walidator potrzebuje schematu pól, zanim zgłosi brakujące referencje albo pary.
- LlamaLogic nie dekoduje każdej tabeli SimData w zainstalowanej wersji gry; `VECTOR` w jednym realnym schemacie powoduje błąd dekodowania.
- Dekodowanie SimData przez `DataModel` jest synchroniczne i ładuje cały model; walidacja projektu próbuje round-trip, a ograniczenia dekodera są ostrzeżeniem zamiast twardego błędu. Dokumentacja LlamaLogic wskazuje, że niektóre SimData lub CombinedTuning mogą być kosztowne do zdekodowania.
- Obecny edytor obrazów zapisuje DST, PNGImage, LRLE i rozpoznane TS4 translucent JPEG. RLE2, RLES i PNGImage2 wymagają oddzielnych sprawdzonych dekoderów/enkoderów.
- Indeks zwraca wszystkich dostawców i wskazówki warstw. Nie twierdzi, który pakiet wygra, dopóki kolejność nie zostanie sprawdzona przez runtime.
- In-game smoke test w izolowanym profilu nie został wykonany. Build i indeks nie zapisują do aktywnego folderu Mods.

## Rozpoznanie kodeków RLE

Zainstalowany LlamaLogic.Packages 3.8.2 udostępnia konwersję PNG dla DST, ale nie znalazłem w jego API wrapperów RLE2, LRLE ani RLES. Bezpośrednia próba `GetDdsAsPngAsync` na realnym zasobie każdego typu zwróciła `Unknown image format`. Indeks wersji gry 1.128.90.1030 zawiera 112 859 providerów RLE2, 8 974 LRLE i 8 659 RLES; nie zawiera providerów PNGImage2. S4MT ma teraz własny kodek LRLE: dekoduje v1 i v2, zapisuje v2, sprawdza wszystkie mipy oraz wymaga dokładnego odczytu RGBA po ponownym otwarciu pakietu. Dwa realne zasoby gry, oba 1024×2048 z dziewięcioma mipami, przechodzą eksport. Weryfikacja w grze nadal jest otwarta.

Repozytorium [Sims4Tools / s4pi](https://github.com/s4ptacle/Sims4Tools) zawiera wrappery obrazów RLE, lecz projekt wskazuje .NET 4.0 i licencję GPLv3. Nie dodaję go jako zależności do S4MT bez decyzji o licencji i sposobie dystrybucji.

Repozytorium [@s4tk/images](https://github.com/sims4toolkit/images) jest eksperymentalnym pakietem TypeScript. Jego obecny moduł eksportuje `DdsImage`, więc nie zamyka braku kodeków pikselowych RLE2 i RLES. S4MT ma teraz niezależną walidację struktury DXT5 opartą na 20 przypiętych fixture'ach każdego typu. Dekoder pikseli pozostaje zablokowany do czasu zdobycia niezależnego wzorca pikseli; obsługa RLES wymaga też zachowania osobnej maski.

## Zasada rozbudowy

Każdy nowy kodek dostaje realny fixture, zachowuje TGI jawnie i ma weryfikację po ponownym otwarciu. Nie dodajemy wspólnego systemu pluginów: obecne formaty mają różne modele i różne warunki poprawnego round-trip.

## Dokumentacja bazowa

- [LlamaLogic.Packages 3.8.2 na NuGet](https://www.nuget.org/packages/LlamaLogic.Packages/3.8.2)
- [API DataBasePackedFile](https://github.com/llama-logic/llamalogic/blob/main/docs/packages/LlamaLogic.Packages.DataBasePackedFile.html)
- [API ResourceType](https://llama-logic.github.io/LlamaLogic/packages/LlamaLogic.Packages.ResourceType.html)
- [API DataModel](https://github.com/llama-logic/llamalogic/blob/main/docs/packages/LlamaLogic.Packages.Models.Data.DataModel.html)
- [Dokumentacja ImageSharp](https://docs.sixlabors.com/articles/imagesharp/)
