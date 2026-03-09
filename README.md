# Kodtest: Tidsseriemodellering for energiförbrukning

Denna repo implementerar en komplett, körbar pipeline i .NET 10 för Del 1-4:
- Datainläsning + preprocessing
- Feature engineering + leakage-safe supervised matris
- Modellering med två modeller (BaselineSeasonal och FastTreeRecursive)
- Utvärdering med MAE, RMSE och MAPE
- Även en modul för producera diagnostiska artefakter 

## Körning

Krav: .NET 10 SDK

Kör hela pipelinen (Del 1-4):

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

Kör med permutation feature importance (PFI):

```bash
dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- --pfi
```

Köra part3 med valfri PFI-horisont (standard är `t+1`, tillåtet intervall `1..192`):

```bash
dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- part3 --pfi --pfi-horizon 96
```

Snabb smoke-körning genom att begränsa Part 3-underlaget (behåller både Train/Validation):

```bash
dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- all --max-rows 5000
```

Flaggan `--max-rows` kan användas med både `all` och `part3` för snabbare verifieringskörningar.

För guide till designval se `docs/ARCHITECTURE.md`.

Genererade filer skrivs till `artifacts/`.
Vid `all`- och `part3`-körningar skrivs även `run_manifest.json` med effektiva körinställningar (CLI-args, PFI-horisont, model defaults, paths och git metadata) for reproducerbarhet.

Sammanslagna feature-matrisen finns i `part1_feature_matrix.csv` och matrisen med definition av kopplingen indata till utdata finns i `part2_supervised_matrix.csv`.

Efterfrågad jämförelse av MAE,RMSE och MAPE finns i `part4_metrics.csv` och urval av predikterade vs faktiska värden finns i `part4_fasttree_validation_tplus92_48h.csv` med tillhörande .svg-plot 
och `part4_baselineseasonal_validation_tplus92_48h.csv` med tillhörande .svg-plot.

## Del 5 - Reflektion och dokumentation

### 1. Vilka modelleringsval gjorde du och varför?
Jag valde att implementera naiv säsongmodell och rekursiv FastTree för att fokusera på en robust implementation.
Modelleringsvalen jag gjorde i form av features var de som specificerades i uppgiften. 

Permutation Feature Importance (PFI) används för att se vilka variabler som påverkar mest och modellinstabilitet genom multi-seed PFI (PFI körs flera gånger, se PipelineConfig). Valde PFI framför impurity-mått då det svarar på frågan vilka features som pårverkar prognostisering mest, istället för impurity-mått som svarar på frågan vilka features som påverkar trädkonstruktionen mest.  

Träning och validerings split görs innan preprocessing för att kunna rensa validerings set från forwardfill mellan träning och validerings set, viktig princip för att hantera mer komplicerad preprocess-metodik. På samma sätt förhindras läckage mellan träning och validering genom att ta bort validerings punkter som ligger inom prediktionshorisonten t+192 från gränsen mellan träning och validerings set, då träning på targets som också finns i validerings setet innebär läckage av information.

### 2. Vilka features visade sig viktigast (eller bedöms vara viktigast) och hur motiverar du det?
Enligt PFI baserat på validerings-setet med en prediktions horizont på 24h (96 steg) med permutation count 10 är de viktigaste variablerna `TargetLag192`, `TargetLag672`, `HourCos`,`Temperature`. Lägre men ändå till synes signifikant effekt har `TargetLag192Mean96`,`HourOfDay`, och `HourSin`. 

`TargetLag192` är senaste tillgängliga informationen på target i feature-setet så det är den mest uppdaterade bilden av nivån av förbrukningen. Fångar också variationen i förbrukning från dag till dag givet samma tid, låg förbrukning iförrgår kl 5 på morgonen innebär förmodligen också lågförbrukning idag.
`TargetLag672` tar hand om säsongseffekt på veckobasis som kan orsakas av helg vs veckodag. 
`HourCos` hjälper modellen att representera tid på dygnet vilket är relevant eftersom förbrukning går upp på dagen och ner på natten. Tillskillnad från `TargetLag192` påverkas inte `HourCos` av idiosynkratiska händelser. Vid t-192 kan target varit ovanligt hög pga oförutsedda händelser, det kan forfarande betyda att det är natt/morgon.     
`Temperature` kan förklara kortsiktig idiosynkratisk variation och exogena chocker i förbrukning. Den fungerar också som proxy för årstid. Årstidseffekten är tydlig i target-serien när den plottas över tid. 

### 3. Vad skulle du göra annorlunda eller lägga till med mer tid?
- Köra tidsserie-CV med flera rullande foldar. I det fallet behöver man rensa ut valideringspunker i slutet av varje fold pga rullande features.
- Införa 3-way split (Train/Validation/Holdout): använda Validering för hyperparameter-tuning och feature selection, och en separat Holdout för slutlig utvärdering.
- Träna om en reducerad modell efter feature importance/feature selection och undersökning av korrelerade features och jämföra den mot full modell samt baseline på Holdout.
- Modellera och prediktera exogena variabler för att kunna rulla fram dessa i rekursiva loopen. 
- Lägga till probabilistiska prognoser (prediktionsintervall), inte bara punktprognos.
- Implementera fler modeller
- Parallellisera genom anchors-partitioner 
- Mutations testning för mäta testkvalitet 

### 4. Hur skulle du hantera konceptdrift?
Kombinera örvaking, snabb detektion och kontrollerad omträning:
- Övervaka live-MAE/RMSE/MAPE per tidsfack (timme, veckodag, säsong) och residualers bias.
- Sätta trösklar för driftlarm
- Omträna på rullande fönster enligt schema eller eventdrivet vid driftlarm.
- Ny modell skuggkörs innan promotion.
- Versionshantera data, features och modeller for reproducerbar rollback.

### 5. Hur skalbar är lösningen for applicering pa stort antal tidsserier?
Pipelinen är modulär (Part 1–4) men körs helt sekventiellt med alla datastrukturer i minne. Flaskhalsen är Part 3-inferens: rekursiv 192-stegs rollout per anchor-punkt som tar ~50% av körtiden. Varje anchor-punkts (t) prediktioner är oberoende av andra anchors prediktioner, vilket ger naturlig parallelliserbarhet via Parallel.ForEach — dock inte stegen inom en rollout, som är sekventiella pga rekursionen. Modellträning (Baseline + FastTree + ...) kan såklart också parallelliseras.