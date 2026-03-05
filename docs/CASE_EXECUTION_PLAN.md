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

## Part 2 (Del 2) – Baseline forecasting and evaluation

### Goal (Mål)

Train and evaluate at least one simple baseline model using the preprocessed Part 1 dataset, with leakage-safe validation and reproducible artifacts.

### Assumptions (Antaganden)

- Input comes from Part 1 persisted output (`artifacts/part1_feature_matrix.csv`).
- Validation window remains the last 30 days (time-based split).
- Rows excluded in Part 1 due to training-sourced target imputation are already removed from the persisted modeling dataset.
- Evaluation focuses on deterministic, explainable baselines before introducing advanced models.

### In scope

1. Load the Part 1 feature matrix and construct model-ready matrices (`X`, `y`).
2. Implement one or more baseline forecasting approaches (for example persistence and/or linear regression baseline).
3. Train on pre-validation window and evaluate on the last-30-days validation window.
4. Persist evaluation outputs (metrics + prediction file + run metadata).
5. Ensure the full flow is reproducible from CLI.

### Deliverables (Leverabler)

1. Baseline training/evaluation pipeline entry point.
2. Persisted prediction artifact for validation window.
3. Persisted metrics summary artifact.
4. At least one automated test for split correctness and one for metric calculation behavior.

### Implementation steps (Implementation steg)

1. Define Part 2 data contract
	- Parse feature matrix rows into typed model-input DTOs.
	- Ensure target and feature columns map explicitly and deterministically.
2. Implement split-first evaluation setup
	- Reconstruct/confirm validation boundary (last 30 days).
	- Build train/validation partitions strictly by `utcTime`.
3. Implement baseline model(s)
	- Add a deterministic naive baseline (for example last-value/persistence).
	- Optionally add a simple statistical baseline (for example linear regression) if required by assignment rubric.
4. Implement evaluation metrics
	- Compute primary regression metrics (for example MAE, RMSE, MAPE where valid).
	- Handle edge cases safely (zero targets, empty partitions).
5. Persist Part 2 artifacts
	- Save validation predictions with timestamps.
	- Save metrics summary and run metadata (input path, split boundary, model id, timestamp).

### Out of scope

- Hyperparameter tuning and large model search.
- Advanced ensembles and production serving/API concerns.
- Assignment Parts 3–5.

### Done criteria (Klart-kriterier)

- [ ] Part 2 pipeline reads Part 1 persisted feature matrix successfully.
- [ ] Train/validation split is strictly time-based and leakage-safe.
- [ ] At least one baseline model is trained and evaluated end-to-end.
- [ ] Metrics artifact and predictions artifact are persisted to `artifacts/`.
- [ ] At least one test verifies split boundary behavior.
- [ ] At least one test verifies metric computation correctness.
- [ ] Commands for running Part 2 are documented.

### Test cases (minimum) (Testfall)

1. Split boundary correctness
	- Given known timestamps, rows before boundary go to train and rows on/after boundary go to validation.
2. Baseline prediction behavior
	- Given a simple deterministic series, baseline outputs expected predictions.
3. Metric calculation
	- Given known `yTrue/yPred`, metrics match expected numeric values within tolerance.
4. Artifact persistence
	- Running Part 2 produces prediction and metrics files with expected headers/fields.

### Verification (Verifiering)

- `dotnet restore ExpekraCase.sln`
- `dotnet build ExpekraCase.sln`
- `dotnet test ExpekraCase.sln`
- `dotnet test ExpekraCase.sln --collect:"XPlat Code Coverage"`
