# Kodtest: Tidsseriemodellering for energiforbrukning

Denna repo implementerar en komplett, körbar pipeline i .NET 10 for Del 1-4:
- Datainläsning + preprocessing
- Feature engineering + leakage-safe supervised matris
- Modellering med två modeller (BaselineSeasonal och FastTreeRecursive)
- Utvärdering med MAE, RMSE och MAPE
- Även en modul för producera diagnostiska artefakter 

## Körning

Krav: .NET 10 SDK

Kör hela pipelinen (Del 1-4 + diagnostics):

```bash
dotnet run --project src/Forecasting.App/Forecasting.App.csproj
```

Kör enskilda delar:

```bash
dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- part1
dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- part2
dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- part3
dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- part4
dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- diagnostics
```

Kör Part 3 med permutation feature importance (PFI):

```bash
dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- part3 --pfi
```

Valfri PFI-horisont (standard är `t+1`, tillåtet intervall `1..192`):

```bash
dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- part3 --pfi --pfi-horizon 96
```

Genererade filer skrivs till `artifacts/`.
Vid `all`- och `part3`-körningar skrivs även `run_manifest.json` med effektiva körinställningar (CLI-args, PFI-horisont, model defaults, paths och git metadata) for reproducerbarhet.

## Del 5 - Reflektion och dokumentation

### 1. Vilka modelleringsval gjorde du och varfor?
Jag valde två modeller for tydlig jämforelse:
- `BaselineSeasonal`: predikterar per veckodag/timme/minut utifrån historiskt medel för motstvarande tidpunkt. Den ar enkel, robust och fungerar som minimikrav-baseline.
- `FastTreeRecursive` (ML.NET): en enstegsmodell som rullas fram rekursivt over 192 steg (48h i 15-minutersupplosning).

Skälet till dessa val var att få en tydlig trade-off mellan enkel tolkbar referensmodell och en starkare ML-modell. På valideringsdatan blev FastTree klart bättre i denna implementation (se `artifacts/part4_metrics.csv`):
- BaselineSeasonal: MAE 9273.88, RMSE 11361.91, MAPE 17.95
- FastTreeRecursive: MAE 2270.13, RMSE 3215.10, MAPE 4.56

### 2. Vilka features visade sig viktigast (eller bedoms vara viktigast) och hur motiverar du det?
Jag har inte kört explicit feature-importance-export i denna version, men utifrån problemtyp och resultat bedömer jag följande som viktigast:
- `TargetLag192` och `TargetLag672`: fångar stark dygns- och veckosäsong i energiförbrukning.
- Rullande statistik (`TargetMean16/96`, `TargetStd16/96`): ger lokal nivå och volatilitet, vilket hjälper modellen att anpassa sig till kortsiktiga regimskiften.
- Kalender/cykliska features (`HourSin/Cos`, `WeekdaySin/Cos`, helgdag): beskriver periodiska mönster som inte fullt ut förklaras av laggar.
- Exogena features (`Temperature`, `Windspeed`, `SolarIrradiation`): viktiga framför allt under väderkansliga perioder, men ofta sekundara till laggar i kort horisont. Temperature fångar även tid på året dynamiken som syns tydligt på en plot över target över tid.

### 3. Vad skulle du göra annorlunda eller lägga till med mer tid?
- Köra tidsserie-CV med flera rullande foldar (inte bara en valideringssplit).
- Hyperparamter-tuning for FastTree samt jämforelse mot direct multi-step strategi.
- Hantera framtida exogena variabler mer explicit (prognoser/scenarier) i stället för fallback där data saknas.
- Lägga till probabilistiska prognoser (prediktionsintervall), inte bara punktprognos.
- Performance profiling
- Implementera preprocessing och fallback lineage trace

### 4. Hur skulle du hantera konceptdrift?
Jag skulle kombinera overvaking, snabb detektion och kontrollerad omträning:
- Övervaka live-MAE/RMSE/MAPE per tidsfack (timme, veckodag, säsong) och residualers bias.
- Sätta trösklar för driftlarm
- Omträna på rullande fönster enligt schema eller eventdrivet vid driftlarm.
- Kör champion/challenger-upplagg: ny modell skuggkors innan promotion.
- Versionshantera data, features och modeller for reproducerbar rollback.

### 5. Hur skalbar ar lösningen for applicering pa stort antal tidsserier?
Nuvarande losning ar god for enskild/fåtal tidsserier, men skalar till många serier med några tillägg:
- Partionera per serie-id i dataflödet och kör feature-byggning/träning parallellt.
- Batcha inferens och skriv artifacts streamat (för att undvika hög minneslast).
- Använd en hybridstrategi: global modell for "long-tail" serier och serie-specifika modeller for stora/affärskritiska serier.
- Automatisera omträning och modellval per serie med gemensam evalueringsstandard.

Sammanfattning: arkitekturen ar modulart uppdelad (Part1-Part4) och passar bra som grund for produktion, men for storskalighet behovs tydligare orkestrering, modellstyrning och drift-övervakning.
