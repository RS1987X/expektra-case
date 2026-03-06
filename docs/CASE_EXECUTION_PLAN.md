# CASE_EXECUTION_PLAN.md

## Part 1 (Del 1) – Data ingestion and preprocessing

### Goal (Mål)

Build a simple, working pipeline for assignment Part 1 (Del 1).

### Assumptions (Antaganden)

- The input file is `data/testdata.csv`.
- Holiday source file is `data/holidays.public.csv`.
- The `utcTime` column is interpreted as UTC.
- Missing `Target` values are handled with forward-fill.

### In scope

1. Load `data/testdata.csv` into typed C# objects.
2. Handle missing values in `Target` (use forward-fill consistently).
3. Generate calendar features per timestep:
	- hour of day (0-23)
	- minute of hour (0/15/30/45)
	- day of week
	- is holiday (boolean, a simple hardcoded Swedish holiday list is enough)
4. Generate sinus/cosinus encodings for hour and weekday.
5. Deliver a complete feature matrix ready for the next part.

### Deliverables (Leverabler)

1. Typed input models for raw data.
2. A preprocessed feature row per timestep.
3. A simple pipeline/method that runs CSV -> features.
4. At least one test that verifies parsing + preprocessing.

### Implementation steps (Implementation steg)

1. Define data models
	- Raw row with `utcTime`, `Target`, `Temperature`, `Windspeed`, `SolarIrradiation`.
	- Feature row with calendar and cyclical features.
2. Implement CSV ingestion
	- Read all rows in time order.
	- Handle parse errors clearly (fail fast or explicit validation).
3. Implement missing value handling
	- Forward-fill on `Target`.
	- If the first value is missing, use the first following valid value.
4. Generate features per row
	- `HourOfDay`, `MinuteOfHour`, `DayOfWeek`, `IsHoliday`.
	- `HourSin`, `HourCos`, `WeekdaySin`, `WeekdayCos`.
	- `IsHoliday` is derived from `data/holidays.public.csv` using `Country=SE` and `StartDate`.
	- Ignore malformed holiday rows that cannot be parsed safely.
5. Build feature matrix
	- Return a collection of feature rows in the same time order.
	- Ensure output row count = input row count.

### Out of scope

- Assignment Parts 2–5 (Del 2–5).
- Model training and evaluation.
- API, Docker, and other infrastructure concerns.

### Done criteria (Klart-kriterier)

- [ ] CSV is parsed into typed objects without errors.
- [ ] Missing `Target` values are handled with forward-fill according to assumptions.
- [ ] All Part 1 (Del 1) features exist in output with expected ranges.
- [ ] `HourSin`, `HourCos`, `WeekdaySin`, `WeekdayCos` are within [-1, 1] (allow tiny numeric tolerance, e.g. ±1e-9).
- [ ] `IsHoliday` uses `data/holidays.public.csv` (`Country=SE`, `StartDate`) and tolerates malformed rows.
- [ ] The feature matrix is complete and in correct time order.
- [ ] At least one test verifies parsing + preprocessing flow.
- [ ] Non-obvious design decisions are commented near the code.

### Test cases (minimum) (Testfall)

1. CSV parsing
	- Given a valid test file -> correct row count and correctly mapped fields.
2. Missing Target forward-fill
	- Given gaps in `Target` -> gaps are filled with latest valid value.
3. Feature generation
	- Given a known timestamp -> correct hour/minute/weekday + sin/cos values.
	- Assert cyclical outputs are finite and within [-1, 1] with tolerance.
4. Holiday feature
	- Given known Swedish holiday dates -> `IsHoliday=true`; otherwise false.
	- Malformed rows in `data/holidays.public.csv` do not crash preprocessing.

### Verification (Verifiering)

- `dotnet restore ExpekraCase.sln`
- `dotnet build ExpekraCase.sln`
- `dotnet test ExpekraCase.sln`

## Part 2 (Del 2) – Feature engineering and data structure

### Goal

Implement Part 2 according to the assignment: create lagged target features, rolling statistics, and a clear data structure for multi-step prediction (`t+1` to `t+192`).

### Assumptions

- Input to Part 2 is the Part 1 output (`artifacts/part1_feature_matrix.csv`) after deduplication and Part 1 filtering.
- Time resolution is 15 minutes per step.
- Forecast horizon in Part 2 is 192 steps (48 hours), and labels must include every step from `t+1` through `t+192` (not only the endpoint `t+192`).
- Part 2 must be leakage-safe: each feature at time `t` may only use information up to and including `t`.

### In scope

1. Create target lags:
	- `TargetLag192` (same time two days ago)
	- `TargetLag672` (same time last week)
2. Create rolling statistics for target:
	- mean and standard deviation over 4h (16 steps)
	- mean and standard deviation over 24h (96 steps)
	- computed from history only (no future information)
3. Structure a supervised dataset for multi-step prediction:
	- input at time `t` -> output vector `Target(t+1..t+192)`
	- define exactly which rows are valid based on lag/horizon requirements
4. Persist Part 2 dataset artifacts for the next step.

### Deliverables

1. Typed Part 2 model/input DTO for feature set including lags and rolling statistics.
2. Typed Part 2 label DTO (or equivalent structure) for the 192-step target vector.
3. Pipeline/method that produces Part 2 training-ready rows.
4. Persisted Part 2 feature+label matrix (CSV or equivalent) in `artifacts/`.
5. At least one test for lag/rolling correctness and one test for multi-step label mapping.

### Ambiguity-resolving definitions

- `Lag192` for row `i` uses `Target` from row `i-192`; if missing, the row is invalid for the Part 2 dataset.
- `Lag672` for row `i` uses `Target` from row `i-672`; if missing, the row is invalid for the Part 2 dataset.
- Rolling 4h/24h is computed over the previous 16/96 target values up to and including time `t` (no lookahead).
- Standard deviation is defined as population standard deviation (`n` in the denominator) computed within each rolling window only (not across the full time series).
- A row at time `t` is included only if:
	1) all required lag/rolling values exist,
	2) the full label horizon `t+1..t+192` exists in the dataset.
- Here, `windowRequirement` means the largest rolling lookback in steps (currently 96 from the 24h window). Therefore, with current settings, row count is `N - max(672, 96) - 192 = N - 864`, adjusted for any additional invalid rows.

### Split-safe multi-step mapping rule (purging)

- Use anchor-based samples: each anchor at time `t` maps to one full target vector `y(t+1..t+192)`.
- To avoid leakage, split eligibility is based on the label horizon, not only on anchor timestamp.
- Let `v0` be validation start timestamp and `H = 192`.
	- Training anchors must satisfy: `t + H < v0`.
	- Validation anchors must satisfy: `t >= v0` and `t + H <= seriesEnd`.
- This implies a purge zone before validation: anchors in `(v0 - H, v0)` are excluded from both train and validation because their labels would overlap validation.
- Do not use partial-horizon labels in Del 2; keep only anchors with a complete 192-step output vector.

### Implementation steps

1. Define Part 2 records/DTOs
	- input features at time `t` and label vector for `t+1..t+192`.
2. Implement lag feature generation
	- compute `TargetLag192` and `TargetLag672` with safe indexing.
3. Implement rolling feature generation
	- compute `TargetMean16`, `TargetStd16`, `TargetMean96`, `TargetStd96` causally.
4. Implement multi-step label mapping
	- build target vector with exactly 192 steps per valid row.
5. Persist Part 2 artifacts
	- write features+labels in artifact format for Part 3 modeling.

### Out of scope

- Model training (Part 3).
- Model comparison/metrics (Part 4).
- README reflection/documentation (Part 5).

### Done criteria

- [ ] `TargetLag192` and `TargetLag672` exist and map correctly to historical rows.
- [ ] `TargetMean16/Std16` and `TargetMean96/Std96` exist and are computed causally.
- [ ] Part 2 dataset maps `X(t)` to `y(t+1..t+192)` with no lookahead in features.
- [ ] Valid/invalid rows are handled deterministically and documented.
- [ ] Part 2 artifact is persisted in `artifacts/` for Part 3.
- [ ] At least two tests cover feature and label correctness.

### Test cases (minimum) (Testfall)

1. Lag correctness
	- Given a synthetic series with known values -> `Lag192`/`Lag672` match expected historical indices exactly.
2. Rolling statistics correctness
	- Given a known sequence -> mean/std for 16 and 96 steps match expected values within tolerance.
3. Multi-step label mapping
	- Given a known sequence -> label vector contains exactly the next 192 target values in correct order.
4. Boundary handling
	- Rows near start/end are excluded correctly when lag or horizon is missing.

### Verification (Verifiering)

- `dotnet restore ExpekraCase.sln`
- `dotnet build ExpekraCase.sln`
- `dotnet test ExpekraCase.sln`
- `dotnet test ExpekraCase.sln --collect:"XPlat Code Coverage"`

## Part 3 (Del 3) – Model implementation

### Goal

Implement at least two forecasting models for multi-step prediction using the Part 2 dataset, where at least one model is an ML model.

### Assignment-aligned model choices

To satisfy Del 3 with clear comparability and manageable implementation effort in .NET:

1. **Model A (Baseline): Naive seasonal profile**
	- Predict next 48h (192 steps at 15-minute resolution) as a full horizon vector `t+1..t+192`, using historical average for each future step’s weekday + quarter-hour bucket.
	- Serves as reference baseline.
2. **Model B (ML): ML.NET FastTree regression**
	- Use `Microsoft.ML` FastTree as the required ML model.
	- Implement recursive multi-step forecasting (iteratively predict one step ahead, feed prediction into lag-dependent features for next step).

### Assumptions

- Input is `artifacts/part2_supervised_matrix.csv` with split labels and horizon targets.
- Forecasting horizon for implementation and comparison is 192 steps (48h), aligned with Del 2 output shape.
- Validation split from Del 2 is respected (train rows only for fitting; validation for evaluation in Del 4).
- No external APIs/cloud services are required.

### In scope

1. Add model abstractions and implementations for:
	- Seasonal baseline
	- ML.NET FastTree
2. Add training/inference pipeline wiring in C#.
3. Persist prediction outputs for downstream evaluation (Del 4).
4. Add tests that validate core model plumbing and output contracts.

### Out of scope

- Full model comparison/reporting metrics tables (Del 4).
- Final reflective write-up (Del 5).
- ONNX/LSTM path (Alternative C), unless explicitly selected later.

### Ambiguity-resolving definitions

- “At least two models, one ML” is satisfied by **Baseline + FastTree**.
- For multi-step strategy in FastTree, choose **recursive strategy** for Part 3 (single-step learner rolled forward to 192 steps).
- Baseline prediction key is `(DayOfWeek, HourOfDay, MinuteOfHour)`; if key is unseen in training, fallback to global training mean.
- Predictions are generated for all 192 horizons, producing a deterministic vector in timestamp order.
- Baseline and FastTree are compared on the same output contract: one 192-step prediction vector per anchor (`t+1..t+192`).
- FastTree training uses only rows with `Split=Train`; validation rows are never used for fitting.
- Recursive roll-forward updates target-derived state only (lag/rolling target features), while calendar features are derived from forecast timestamps.
- Default exogenous strategy for recursive inference is to use known future exogenous values from the corresponding anchor/horizon row context in the prepared Part 2 matrix; if unavailable, use deterministic carry-forward from the latest known value and log this fallback in summary output.
- Baseline fallback policy is deterministic: unseen seasonal key -> global train mean; if train set is empty, fail fast with a clear error.
- The assignment wording “next 48h” is implemented as exactly `192` steps at 15-minute resolution.

With these defaults, Part 3 implementation ambiguities are considered resolved unless explicitly overridden by a new requirement.

### Deliverables

1. Typed model interfaces/contracts (fit + predict horizon).
2. Baseline model implementation.
3. ML.NET FastTree model implementation.
4. Prediction artifact(s) in `artifacts/` (CSV/JSON) with:
	- anchor timestamp
	- model name
	- predicted `t+1..t+192`
5. Unit tests for:
	- baseline behavior and fallback
	- model output shape and ordering
	- deterministic prediction contract

### Proposed code structure

- `src/Forecasting.App/Part3Modeling.cs`
	- model contracts
	- training data mapping helpers
	- baseline + FastTree implementations
- `src/Forecasting.App/Program.cs`
	- add `part3` mode to run model training + inference artifact generation
- `tests/Forecasting.App.Tests/Part3ModelingTests.cs`
	- focused unit tests for model logic and output contracts

### Implementation steps

1. Define Part 3 DTOs/contracts
	- model input row representation
	- prediction vector result (`192` outputs)
	- model interface with `Train(...)` and `PredictHorizon(...)`
2. Implement baseline model
	- aggregate train rows by `(DayOfWeek, HourOfDay, MinuteOfHour)`
	- generate 192-step forecast for each anchor
	- add global-mean fallback
3. Implement FastTree recursive model
	- train one-step target model using Part 2 features
	- recursively generate horizon predictions with lag updates from predicted values
4. Persist artifacts
	- write predictions in stable schema for Del 4 metric computation
5. Add tests
	- verify output vector length and temporal ordering
	- verify baseline fallback path
	- verify recursive path produces finite outputs and stable shape

### Done criteria

- [ ] Two models are implemented and runnable from the CLI.
- [ ] At least one model uses ML.NET (`Microsoft.ML`).
- [ ] Baseline model predicts using historical seasonal grouping logic.
- [ ] FastTree model generates 192-step forecasts via recursive strategy.
- [ ] Prediction artifacts are persisted for Del 4 evaluation.
- [ ] Tests cover baseline + ML output contracts and pass.

### Test cases (minimum)

1. Baseline seasonal mapping
	- known bucket -> expected average prediction
	- unseen bucket -> global-mean fallback
2. ML output contract
	- for valid anchor, output length is exactly 192
	- all predicted values are finite numbers
3. Determinism and shape
	- repeated inference with fixed inputs returns same vector length/order
	- artifact writer includes expected headers/columns

### Verification (Verifiering)

- `dotnet restore ExpekraCase.sln`
- `dotnet build ExpekraCase.sln`
- `dotnet test ExpekraCase.sln`
- `dotnet test ExpekraCase.sln --collect:"XPlat Code Coverage"`
- `dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- part3 <part2_input_csv> <predictions_output_csv> <summary_output_json>`
