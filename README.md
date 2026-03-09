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

Snabb smoke-körning genom att begränsa Part 3-underlaget (behåller både Train/Validation):

```bash
dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- all --max-rows 120
```

Flaggan `--max-rows` kan användas med både `all` och `part3` för snabbare verifieringskörningar.

For utvecklare: guide for att lägga till en ny Part 3-modell finns i `docs/ARCHITECTURE.md` under section `Adding a Part 3 Model`.

Genererade filer skrivs till `artifacts/`.
Vid `all`- och `part3`-körningar skrivs även `run_manifest.json` med effektiva körinställningar (CLI-args, PFI-horisont, model defaults, paths och git metadata) for reproducerbarhet.

## Del 5 - Reflektion och dokumentation

### 1. Vilka modelleringsval gjorde du och varfor?
Jag valde att fokusera på Naiv Sässongmodell och rekursiv FastTree för att kunna implementera robust.
Modelleringsvalen jag gjorde i form av features var i princip de specificerades i uppgiften. 

I rekursiva ML-modellen propagerar vi bara fram predikterade värden av target, features som härleds från target och kalender-features som är kända vid prediktionstiden. Exogena variabler är frusna vid prediktionstidpunkt.  

Träning och validerings split görs innan preprocessing för att kunna rensa validerings set från forwardfill mellan träning och validerings set, . På samma sätt förhindras läckage mellan träning och validering genom att ta bord validerings punkter som ligger inom prediktionshorisonten t+192 från gränsen mellan träning och valideringsset. 

### 2. Vilka features visade sig viktigast (eller bedöms vara viktigast) och hur motiverar du det?
Jag har inte kört explicit feature-importance-export i denna version, men utifrån problemtyp och resultat bedömer jag följande som viktigast:
- `TargetLag192` och `TargetLag672`: fångar stark dygns- och veckosäsong i energiförbrukning.
- Rullande statistik (`TargetMean16/96`, `TargetStd16/96`): ger lokal nivå och volatilitet, vilket hjälper modellen att anpassa sig till kortsiktiga regimskiften.
- Kalender/cykliska features (`HourSin/Cos`, `WeekdaySin/Cos`, helgdag): beskriver periodiska mönster som inte fullt ut förklaras av laggar.
- Exogena features (`Temperature`, `Windspeed`, `SolarIrradiation`): viktiga framför allt under väderkansliga perioder, men ofta sekundara till laggar i kort horisont. Temperature fångar även tid på året dynamiken som syns tydligt på en plot över target över tid.

### 3. Vad skulle du göra annorlunda eller lägga till med mer tid?
- Köra tidsserie-CV med flera rullande foldar. Kan behöva rensa ut valideringspunker i slutet av varje fold baserat på pga rullande features då.
- Införa 3-way split (Train/Validation/Holdout): använda Validering för hyperparameter-tuning och feature selection, och en separat Holdout för slutlig utvärdering.
- Träna om en reducerad modell efter feature importance/feature selection och jämföra den mot full modell samt baseline på Holdout.
- Modellera och prediktera exogena variabler för att kunna rulla fram dessa i rekursiva loopen. 
- Lägga till probabilistiska prognoser (prediktionsintervall), inte bara punktprognos.
- Implementera fler modeller
- Performance profiling

### 4. Hur skulle du hantera konceptdrift?
Kombinera örvaking, snabb detektion och kontrollerad omträning:
- Övervaka live-MAE/RMSE/MAPE per tidsfack (timme, veckodag, säsong) och residualers bias.
- Sätta trösklar för driftlarm
- Omträna på rullande fönster enligt schema eller eventdrivet vid driftlarm.
- Kör champion/challenger-upplagg: ny modell skuggkors innan promotion.
- Versionshantera data, features och modeller for reproducerbar rollback.

### 5. Hur skalbar är lösningen for applicering pa stort antal tidsserier?
Pipelinen är modulär (Part 1–4) men körs helt sekventiellt med alla datastrukturer i minne. Flaskhalsen är Part 3-inferens: rekursiv 192-stegs rollout per anchor-punkt som tar ~50% av körtiden. Varje anchor-punkts (t) prediktioner är oberoende av andra anchors prediktioner, vilket ger naturlig parallelliserbarhet via Parallel.ForEach — dock inte stegen inom en rollout, som är sekventiella pga rekursionen. Modellträning (Baseline + FastTree) såklart också parallelliseras.

För applicering på stort antal tidsserier krävs dessutom partitionering per serie-id, då pipelinen idag antar en enda tidsserie.
