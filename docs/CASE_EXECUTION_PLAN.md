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
- Canonical 3-way split and purge rules are defined in `3-way split (train/validation/holdout)` at the end of this document.
- Part 2 must apply those rules exactly when assigning `Split` labels.

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
	- Predict next 48h (192 steps at 15-minute resolution) as a full horizon vector `t+1..t+192`, using historical average for each future step’s weekday + hour bucket.
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
- Baseline prediction key is `(DayOfWeek, HourOfDay)`; if key is unseen in training, fallback to global training mean.
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
	- aggregate train rows by `(DayOfWeek, HourOfDay)`
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

## Part 4 (Del 4) – Evaluation

### Goal

Implement a deterministic evaluation slice for the Part 3 model outputs on the validation period, including standard error metrics and a simple model-comparison table.

### Assumptions

- Input predictions are produced by Part 3 from `artifacts/part3_predictions.csv`.
- Ground truth comes from `artifacts/part2_supervised_matrix.csv` (validation anchors and their horizon targets).
- Validation period follows the existing split policy (last 30 days).
- Evaluation horizon remains `192` steps (48h at 15-minute resolution), and includes every step from `t+1` through `t+192` (not endpoint-only), aligned with Parts 2–3.

### In scope

1. Evaluate predictions on validation anchors only.
2. Compute per-model aggregate metrics:
	- MAE (Mean Absolute Error)
	- RMSE (Root Mean Squared Error)
	- MAPE (Mean Absolute Percentage Error)
3. Produce a compact model-comparison table (console and/or artifact).
4. Persist a 48h sample view of predicted vs actual values for inspection.

### Out of scope

- New model training methods or major Part 3 architecture changes.
- Hyperparameter search/optimization loops.
- Full reporting/dashboard system.

### Ambiguity-resolving definitions

- Evaluation unit is each available `(anchor, horizonStep)` pair in validation where both prediction and actual exist.
- Aggregate metrics are computed with micro-averaging across all evaluated points per model (not per-anchor-first averaging).
- Prediction/actual alignment uses deterministic keys: `ModelName`, `AnchorUtc`, and `HorizonStep` on prediction side joined to `AnchorUtc` + horizon index on ground-truth side.
- Duplicate prediction keys are invalid input; fail fast with a clear error instead of arbitrarily selecting one row.
- RMSE uses squared error mean over evaluated points and then square root.
- MAPE excludes rows where `actual == 0` from denominator-based computation and reports the effective sample count used.
- If a model has no evaluable rows, fail fast with a clear error.
- Persisted numeric metrics use deterministic formatting (InvariantCulture with fixed precision) so repeated runs produce stable artifact diffs.
- “Predicted vs actual for 48h” is implemented as a deterministic 192-step window sample from validation (for example first validation anchor), persisted to artifact.

### Deliverables

1. Part 4 evaluator implementation (read predictions + actuals, compute metrics).
2. Persisted summary artifact with per-model MAE/RMSE/MAPE and counts.
3. Persisted 48h predicted-vs-actual sample artifact.
4. CLI wiring for `part4` execution mode.
5. Tests for metric correctness and output contract.

### Proposed code structure

- `src/Forecasting.App/Part4Evaluation.cs`
	- evaluation DTOs and metric computation
	- artifact writers for summary and 48h sample
- `src/Forecasting.App/Program.cs`
	- add `part4` mode to run evaluation pipeline
- `tests/Forecasting.App.Tests/Part4EvaluationTests.cs`
	- metric formula tests and artifact shape/contract checks

### Implementation steps

1. Define evaluation DTOs/contracts
	- prediction point, actual point, per-model metric summary
2. Implement join/alignment logic
	- match prediction rows to actuals by anchor timestamp and horizon step
	- restrict to validation rows only
	- build an indexed ground-truth lookup once (keyed by `AnchorUtc` + `HorizonStep`) and evaluate predictions in a single streaming pass to avoid repeated joins
3. Implement metrics
	- MAE, RMSE, MAPE (+ MAPE denominator count)
4. Add artifact outputs
	- model comparison summary (JSON/CSV)
	- deterministic 48h predicted-vs-actual sample file
5. Add CLI mode + tests
	- `part4` command path in `Program.cs`
	- unit tests for formulas, filtering, and output schema

### Done criteria

- [ ] Part 4 evaluation runs from CLI without manual setup.
- [ ] MAE, RMSE, and MAPE are computed for each model on validation data.
- [ ] Model comparison output is persisted and easily inspectable.
- [ ] A deterministic 48h predicted-vs-actual sample artifact is persisted.
- [ ] MAPE zero-denominator handling is explicit and test-covered.
- [ ] Part 4 tests pass and verify metric/output contracts.

### Test cases (minimum)

1. Metric correctness (small synthetic set)
	- verify MAE, RMSE, and MAPE against hand-calculated expected values
2. MAPE zero handling
	- rows with `actual == 0` are excluded from MAPE denominator and counted
3. Alignment and filtering
	- only validation anchors are included; missing pairs are skipped deterministically
4. Output contracts
	- summary artifact contains expected per-model fields
	- 48h sample artifact contains ordered horizon points with predicted + actual values

### Verification (Verifiering)

- `dotnet restore ExpekraCase.sln`
- `dotnet build ExpekraCase.sln`
- `dotnet test ExpekraCase.sln`
- `dotnet test ExpekraCase.sln --collect:"XPlat Code Coverage"`
- `dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- part3 <part2_input_csv> <predictions_output_csv> <summary_output_json>`
- `dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- part4 <part2_input_csv> <part3_predictions_csv> <part4_metrics_output> <part4_sample_output>`

## Permutation Feature Importance

### Goal

Compute and persist permutation feature importance (PFI) for the FastTree regression model using ML.NET's built-in `mlContext.Regression.PermutationFeatureImportance(...)` API, evaluated on validation data only, so feature influence is inspectable and explainable.

### Assumptions

- PFI is computed on the one-step training data derived from validation-split rows (the same `OneStepTrainingRow` representation used for FastTree training, but restricted to `Split=Validation`).
- ML.NET's built-in PFI implementation is used — no custom shuffle logic.
- The trained `ITransformer` (FastTree model) from Part 3 is reused; PFI does not retrain the model.
- Feature names are derived from the existing `ToFeatureVector` mapping in `Part3Modeling` and must be kept in sync (enforced by a test assertion and a static `FeatureNames` array whose length is verified at compile time via `[VectorType]` consistency).
- PFI measures how much each feature's permutation degrades one-step prediction quality (MAE / RMSE / R²), not recursive multi-step quality. This is a pragmatic trade-off: recursive PFI (re-rolling 192 steps per feature per permutation) would be prohibitively expensive and ML.NET does not support it natively.
- Determinism is provided by the existing `MLContext(seed: 42)` — the PFI API itself does not accept a separate seed parameter.
- PFI runs unconditionally as part of every `part3` execution (no opt-in flag). The cost (10 permutations × 18 features = 180 evaluation passes over validation rows) is acceptable given the dataset size.
- If validation has zero rows, PFI is skipped and `Part3RunResult.FeatureImportance` is set to `null` (consistent with the existing Part 3 guard that throws on empty validation for forecasting, but PFI can degrade gracefully since it is supplementary).

### In scope

1. Add a method to `Part3Modeling` that computes PFI using `mlContext.Regression.PermutationFeatureImportance()`.
2. Return a ranked list of features with per-feature metric deltas (MAE change, RMSE change, R² change) and standard deviations.
3. Persist PFI results as a CSV artifact to `artifacts/part3_feature_importance.csv` (written by Part 3).
4. Diagnostics reads the PFI CSV from disk and generates a horizontal bar chart SVG to `artifacts/diagnostics/`.
5. Include PFI summary in the diagnostics HTML report (omitted gracefully if PFI CSV is absent).
6. Add a `Part3PfiResult` record (or similar) to `Part3RunResult` for in-memory consumers; also persist to CSV for standalone diagnostics.
7. Add unit tests for PFI output contract, artifact generation, and feature name sync.
8. Handle empty validation gracefully: skip PFI, set result to `null`, omit artifacts.

### Out of scope

- Recursive multi-step PFI (shuffling a feature and re-rolling all 192 steps per anchor).
- SHAP values or other model-specific explainability methods.
- Feature selection / automatic feature removal based on PFI results.
- PFI for the baseline seasonal model (not ML-based).

### Ambiguity-resolving definitions

- **Evaluation data for PFI**: validation-split rows converted to `OneStepTrainingRow` (features + `Label = HorizonTargets[0]`). This ensures PFI reflects out-of-sample importance, not training-fit importance.
- **Permutation count**: use `permutationCount: 10` for stable estimates. Determinism is inherited from `MLContext(seed: 42)` (the PFI API does not accept a separate seed parameter).
- **Feature name mapping**: the 18 features in `ToFeatureVector` are mapped to human-readable names by index:

  | Index | Feature Name |
  |-------|-------------|
  | 0 | TargetAtT |
  | 1 | Temperature |
  | 2 | Windspeed |
  | 3 | SolarIrradiation |
  | 4 | HourOfDay |
  | 5 | MinuteOfHour |
  | 6 | DayOfWeek |
  | 7 | IsHoliday |
  | 8 | HourSin |
  | 9 | HourCos |
  | 10 | WeekdaySin |
  | 11 | WeekdayCos |
  | 12 | TargetLag192 |
  | 13 | TargetLag672 |
  | 14 | TargetMean16 |
  | 15 | TargetStd16 |
  | 16 | TargetMean96 |
  | 17 | TargetStd96 |

- **Ranking**: features are ranked by absolute MAE delta (descending). Positive delta = permuting the feature degrades MAE = feature is important.
- **Artifact format**: CSV with columns `Rank;FeatureName;MaeDelta;MaeDeltaStdDev;RmseDelta;RmseDeltaStdDev;R2Delta;R2DeltaStdDev`, semicolon-delimited, InvariantCulture formatting.
- **SVG bar chart**: horizontal bars ranked top-to-bottom by importance (MAE delta), with feature names as y-axis labels.
- **Data flow for diagnostics**: Part 3 writes PFI CSV to `artifacts/part3_feature_importance.csv`. Diagnostics reads this CSV back from disk (new optional input path to `RunDiagnostics`). This keeps the `diagnostics` CLI mode fully standalone — it does not depend on in-memory `Part3RunResult`. If the PFI CSV does not exist, the diagnostics HTML report omits the PFI section gracefully.
- **Empty validation**: if validation has zero rows usable for PFI, `Part3RunResult.FeatureImportance` is `null`, the PFI CSV is not written, and diagnostics omits the PFI section.
- **Feature name sync enforcement**: a unit test asserts that `Part3Modeling.FeatureNames` matches a hardcoded expected list of 18 names and that `FeatureNames.Length` equals the `[VectorType]` dimension on `OneStepTrainingRow.Features`. Any feature addition/removal fails the test, forcing both arrays to be updated together.

### Deliverables

1. `Part3PfiResult` record containing per-feature importance metrics.
2. `ComputePermutationImportance(...)` method in `Part3Modeling` that accepts the trained model, `MLContext`, and validation data, and returns `Part3PfiResult`.
3. PFI CSV artifact: `artifacts/part3_feature_importance.csv`.
4. PFI SVG bar chart: `artifacts/diagnostics/feature_importance.svg`.
5. PFI section in diagnostics HTML report.
6. Unit tests for PFI output shape, ranking, and artifact contracts.

### Proposed code structure

- `src/Forecasting.App/Part3Modeling.cs`
  - Add `Part3PfiFeatureResult` record (feature name, MAE/RMSE/R² deltas + std devs, rank).
  - Add `Part3PfiResult` record (list of feature results, permutation count, evaluation row count).
  - Add static feature name array (kept in sync with `ToFeatureVector`).
  - Add `ComputePermutationImportance(...)` method.
  - Refactor `BuildFastTreeRecursiveModel` to expose the `MLContext` and `ITransformer` (or return them alongside the `FastTreeRecursiveModel`) so PFI can reuse them without retraining.
  - Wire PFI call into `RunModels` and include result in `Part3RunResult`.
- `src/Forecasting.App/PartDiagnostics.cs`
  - Add `ReadFeatureImportanceCsv(string path)` method to parse the Part 3 PFI CSV from disk.
  - Add `WriteFeatureImportanceSvg(...)` method (horizontal bar chart).
  - Add PFI section to HTML report builder (omitted if PFI CSV not found).
  - Extend `RunDiagnostics` signature to accept an optional PFI CSV path (defaulting to `artifacts/part3_feature_importance.csv`).
- `src/Forecasting.App/Program.cs`
  - In `part3` mode: call `ComputePermutationImportance`, persist PFI CSV via `Part3Modeling.WriteFeatureImportanceCsv(...)`.
  - In `diagnostics` mode: read PFI CSV from disk if it exists; pass to diagnostics for SVG/HTML generation.
  - In `all` mode: same as `part3` + `diagnostics` sequentially.
- `tests/Forecasting.App.Tests/Part3ModelingTests.cs`
  - Test PFI result shape: 18 features returned, all named, ranked.
  - Test that PFI result contains finite metric deltas.
- `tests/Forecasting.App.Tests/PartDiagnosticsTests.cs`
  - Test PFI CSV artifact columns and row count.
  - Test PFI SVG artifact is non-empty and contains expected structure.

### Implementation steps

1. Define PFI records/DTOs
   - `Part3PfiFeatureResult(int Rank, string FeatureName, double MaeDelta, double MaeDeltaStdDev, double RmseDelta, double RmseDeltaStdDev, double R2Delta, double R2DeltaStdDev)`.
   - `Part3PfiResult(IReadOnlyList<Part3PfiFeatureResult> Features, int PermutationCount, int EvaluationRowCount)`.
   - Add static `FeatureNames` array to `Part3Modeling` matching `ToFeatureVector` index order.
2. Expose trained model internals for PFI
   - Extend `BuildFastTreeRecursiveModel` or extract a helper so the `MLContext` and `ITransformer` are available after training (currently local variables; need to be returned or stored).
3. Implement `ComputePermutationImportance`
   - Guard: if validation rows are empty, return `null`.
   - Build validation `IDataView` from validation `OneStepTrainingRow` instances.
   - Call `mlContext.Regression.PermutationFeatureImportance(model, validationDataView, labelColumnName: nameof(OneStepTrainingRow.Label), featureColumnName: nameof(OneStepTrainingRow.Features), permutationCount: 10)`. Determinism is inherited from `MLContext(seed: 42)`.
   - Map results by index to `FeatureNames`, rank by `|MaeDelta|` descending.
   - Return `Part3PfiResult`.
4. Integrate into `RunModels`
   - Call `ComputePermutationImportance` after model training.
   - Add `Part3PfiResult` to `Part3RunResult` (or a new extended result record).
5. Add artifact writers
   - `Part3Modeling.WriteFeatureImportanceCsv(...)`: semicolon-delimited CSV with header row, written by Part 3.
   - `PartDiagnostics.ReadFeatureImportanceCsv(...)`: parse PFI CSV back into records for SVG/HTML generation.
   - `PartDiagnostics.WriteFeatureImportanceSvg(...)`: horizontal bar chart SVG (similar pattern to existing `BuildValidationHorizonSvg`).
   - Add PFI section to HTML report (omitted if PFI data is absent).
6. Wire CLI and persistence
   - `part3` mode: compute PFI, persist CSV to `artifacts/part3_feature_importance.csv`.
   - `diagnostics` mode: read PFI CSV from disk (if present), generate SVG to `artifacts/diagnostics/feature_importance.svg`, include in HTML.
   - `all` mode: Part 3 writes CSV, then diagnostics reads it back.
7. Add tests
   - Part3: PFI result shape (18 features), all finite, ranked by MAE delta.
   - Diagnostics: PFI CSV/SVG artifact existence and basic content checks.

### Done criteria

- [ ] PFI is computed using ML.NET's built-in `PermutationFeatureImportance` API on validation data.
- [ ] PFI uses `permutationCount: 10` with determinism from `MLContext(seed: 42)` (PFI API does not accept a separate seed).
- [ ] All 18 features are reported with MAE, RMSE, and R² deltas + std devs.
- [ ] Features are ranked by absolute MAE delta (descending).
- [ ] PFI CSV artifact is persisted to `artifacts/part3_feature_importance.csv`.
- [ ] PFI SVG bar chart is persisted to `artifacts/diagnostics/feature_importance.svg`.
- [ ] PFI section appears in diagnostics HTML report.
- [ ] Feature names in PFI output match `ToFeatureVector` index order.
- [ ] PFI does not retrain the model (reuses trained `ITransformer`).
- [ ] Unit tests verify PFI output contract and artifact generation.
- [ ] Unit test asserts `FeatureNames` matches expected hardcoded list and `[VectorType]` dimension.
- [ ] Empty validation produces `null` PFI result without error.
- [ ] Diagnostics omits PFI section gracefully when PFI CSV is absent.
- [ ] All existing tests continue to pass.

### Test cases (minimum)

1. PFI result shape
   - PFI returns exactly 18 feature results with non-empty names.
   - All metric deltas and std devs are finite numbers.
2. PFI ranking
   - Features are ordered by absolute MAE delta descending (rank 1 = most important).
3. PFI reproducibility
   - Two consecutive PFI runs with the same data produce identical rankings and metric values (deterministic seed).
4. PFI artifact contract
   - CSV has expected header and 18 data rows.
   - SVG is non-empty and contains `<rect` elements (bars).
5. Feature name consistency
   - The feature names in PFI output match a known reference list derived from `ToFeatureVector`.
   - `FeatureNames.Length` equals the `[VectorType(N)]` dimension on `OneStepTrainingRow.Features`.
6. Empty validation
   - When validation has zero rows, `ComputePermutationImportance` returns `null` and no PFI CSV is written.
7. Diagnostics without PFI CSV
   - When PFI CSV does not exist on disk, diagnostics runs successfully and the HTML report omits the PFI section.

### Verification (Verifiering)

- `dotnet restore ExpekraCase.sln`
- `dotnet build ExpekraCase.sln`
- `dotnet test ExpekraCase.sln`
- `dotnet test ExpekraCase.sln --collect:"XPlat Code Coverage"`
- `dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- part3 <part2_input_csv> <predictions_output_csv> <summary_output_json>` (verify PFI CSV appears in artifacts)
- `dotnet run --project src/Forecasting.App/Forecasting.App.csproj -- diagnostics <part2_input_csv> <part3_predictions_csv>` (verify PFI SVG and HTML section)

## 3-way split (train/validation/holdout)

### Goal

Define one canonical, leakage-safe contract for split assignment and evaluation usage across Parts 2-4.

### Scope

1. Centralize deterministic 3-way split rules for anchor eligibility.
2. Define evaluation protocol intent for `Validation` vs `Holdout`.
3. Ensure all related parts reference this section as the source of truth.
4. Keep this behavior optional via CLI (not the default pipeline setting).

### Activation (CLI opt-in, not default)

- 3-way split mode must be explicitly enabled via CLI flag(s); it is not the standard/default run mode.
- When the opt-in flag is absent, pipeline behavior stays on the existing default split/evaluation flow.
- Planned CLI shape (final naming can be adjusted in implementation):
	- split creation toggle: `--three-way-split`
	- evaluation target selection: `--eval-split validation|holdout`

### Intended workflow (model reduction/tuning)

- Purpose: support iterative model tuning/reduction using validation evidence, then unbiased final check on holdout.
- Iterative phase:
	- run with `--three-way-split`
	- use validation outputs (for example error metrics and PFI) to tune/reduce features/model complexity
	- repeat until model is frozen
- Final phase:
	- evaluate the frozen/reduced model once on holdout (`--eval-split holdout`)
	- treat holdout results as final reporting only, not as feedback for more retuning

### Detailed execution workflow (human-in-the-loop feature reduction)

1. Baseline setup (single source input)
	- Keep raw input unchanged (do not create or maintain a manually truncated file under `data/`).
	- Use the existing pipeline input path and enable 3-way behavior through CLI only.
	- If an optional derived split artifact is needed for inspection, write it to `artifacts/`, not `data/`.

2. Create leakage-safe split configuration
	- Run with 3-way mode enabled (for example `--three-way-split`) and explicit holdout size (for example `--holdout-days 30`, final flag name decided in implementation).
	- Ensure split order is deterministic and timestamp-based (`Train` then `Validation` then `Holdout`).
	- Ensure purge zones are applied at both boundaries before assigning split labels.

3. Train reference model(s) on non-holdout training split
	- Train models using only rows eligible for fitting (`Split=Train`) during iterative reduction cycles.
	- Produce standard prediction and summary artifacts as usual.
	- Generate validation-focused diagnostics/PFI outputs used for feature decisions.

4. Validation analysis checkpoint (human decision)
	- Review validation metrics (for example `MAE`, `RMSE`, `MAPE`) and PFI rankings.
	- Decide which features to keep/remove manually.
	- Record chosen feature subset in a reproducible form (CLI include-list or config value).

5. Train reduced model candidate
	- Re-run training with explicit feature subset (for example `--feature-include <comma-separated-features>`, final naming decided in implementation).
	- Evaluate on validation again and compare against previous run.
	- Repeat steps 4-5 until feature subset is frozen.

6. Final training policy before holdout
	- Once subset is frozen, retrain final reduced model on non-holdout data according to selected policy:
		- conservative option: train on `Train` only,
		- final-fit option: train on `Train+Validation`.
	- Policy must be explicit in run notes/artifacts so comparisons remain interpretable.

7. One-time holdout evaluation
	- Run evaluation targeting holdout only (for example `--eval-split holdout`).
	- Report reduced model alongside comparison models on the same holdout points.
	- Persist split-tagged outputs (for example metrics and sample files labeled `holdout`).

8. Guardrail after holdout
	- Do not use holdout outcomes to remove/add features and rerun tuning.
	- If additional tuning is required, start a new experiment cycle and reserve a fresh final-evaluation window.

9. Recommended artifact trail per cycle
	- Validation cycle artifacts: metrics, diagnostics, PFI table, selected-feature list.
	- Final cycle artifacts: frozen feature list, final training policy (`Train` or `Train+Validation`), holdout metrics, holdout prediction-vs-actual sample.
	- Keep artifact naming deterministic and split-tagged to support reproducible comparisons.

### Reproducibility lock (pre/post-human runs must match)

To ensure the split is identical before and after human feature-selection decisions, split-defining inputs must be frozen and verified.

1. Freeze split-defining inputs before the first run:
	- input dataset path
	- input dataset checksum (content hash)
	- `--three-way-split` enabled/disabled
	- `--validation-days`, `--holdout-days`
	- horizon (`H = 192`) and cadence assumptions

2. Persist resolved split boundaries and counts in run manifest:
	- `ValidationStartUtc` (`v0`)
	- `HoldoutStartUtc` (`h0`)
	- `SeriesEndUtc`
	- `TrainAnchors`, `ValidationAnchors`, `HoldoutAnchors`
	- `PurgedBeforeValidation`, `PurgedBeforeHoldout`

3. Compute and persist a split fingerprint:
	- deterministic hash over:
		- input checksum
		- `v0`, `h0`, `H`
		- split counts and purge counts

4. Post-human run guard:
	- compare current split-defining inputs and fingerprint against frozen manifest
	- fail fast on mismatch with clear error (do not continue training/evaluation)

5. Allowed post-human changes:
	- feature subset (for example `--feature-include`)
	- model roster/hyperparameters
	- PFI settings
	- outputs/paths
	- not allowed without starting a new experiment cycle:
		- input dataset content
		- validation/holdout window settings
		- split mode on/off

6. Artifact policy:
	- include split fingerprint in Part 3/Part 4 outputs and summaries
	- require holdout reports to reference the same split fingerprint as the corresponding validation/PFI run

### Canonical definitions

- Use anchor-based samples: each anchor at time `t` maps to one full target vector `y(t+1..t+192)`.
- Split eligibility is based on full label horizon, not only anchor timestamp.
- Deterministic split timestamps:
	- `v0` = validation start timestamp
	- `h0` = holdout start timestamp
	- `H = 192`
	- Required ordering: `trainStart <= ... < v0 < h0 <= seriesEnd`
- Full-horizon split isolation:
	- Training anchors: `t + H < v0`
	- Validation anchors: `t >= v0` and `t + H < h0`
	- Holdout anchors: `t >= h0` and `t + H <= seriesEnd`
- Purge zones:
	- Pre-validation purge: anchors in `(v0 - H, v0)` are excluded.
	- Pre-holdout purge: anchors in `(h0 - H, h0)` are excluded.
- No anchor may belong to multiple splits.
- No split may use partial-horizon labels.

### Evaluation protocol

- `Train`: fitting only.
- `Validation`: iterative model comparison/tuning and diagnostics.
- `Holdout`: final one-time unbiased reporting.
- Protocol order:
	1) run validation evaluation during development,
	2) freeze model choice,
	3) run holdout evaluation once and treat as report output (not tuning input).

### Deliverables

1. Part 2 output includes deterministic `Split` labels (`Train`, `Validation`, `Holdout`) that satisfy the above contract.
2. Part 3 uses only `Split=Train` for fitting; validation/holdout remain out-of-sample.
3. Part 4 evaluates a selected split (`Validation` or `Holdout`) and labels output artifacts by split.
4. CLI contract documents that 3-way split is opt-in and default behavior is unchanged when opt-in flags are omitted.

### Minimum tests

1. Split-boundary tests for both purge zones (`v0 - H`, `h0 - H`).
2. Training-data filter test proving only `Split=Train` is used for model fitting.
3. Evaluation filter test proving Part 4 includes only requested split rows.
4. Guardrail test showing holdout reporting does not alter training/split assignment behavior.
5. CLI behavior test proving 3-way split logic is activated only when opt-in flags are provided.
6. Reproducibility guard test proving post-human run fails when split-defining inputs differ from frozen manifest.
7. Fingerprint consistency test proving identical inputs regenerate identical split fingerprint and split counts.
