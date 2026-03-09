# Data Pipeline Robustness Checks

This document lists the robustness controls currently implemented in the data ingestion and preprocessing pipeline.

## Scope

- Part 1 ingestion and preprocessing (`src/Forecasting.App/Part1Preprocessing.cs`)
- Shared CSV parsing helpers (`src/Forecasting.App/Csv/CsvParsing.cs`)
- Part 2 supervised-dataset construction and leakage controls (`src/Forecasting.App/Part2FeatureEngineering.cs`)
- Shared dedup helper (`src/Forecasting.App/CollectionHelpers.cs`)

## 1) Input parsing and schema guards

- Required-column lookup with deterministic failures.
  - `CsvParsing.FindRequiredColumnIndex(...)`
  - Throws `FormatException` when a required column is missing.

- Strict numeric/int/bool/datetime parsing helpers with line-numbered errors.
  - `CsvParsing.ParseRequiredDouble/Int/Bool/ParseRequiredUtcDateTime`

- Optional non-finite guard for required doubles.
  - `CsvParsing.ParseRequiredDouble(..., rejectNonFinite: true)` supports explicit NaN/Infinity rejection when requested.

## 2) Raw data ingestion robustness (Part 1)

- Blank row tolerance.
  - Empty lines are skipped in `ReadRawDataRows(...)`.

- Minimum column-count check.
  - Raw rows must have at least 5 columns, otherwise fail fast.

- Strict timestamp parsing with explicit accepted formats.
  - Accepted formats: `yyyy-MM-dd HH:mm` and `yyyy-MM-dd HH:mm:ss`.
  - Parsed as UTC (`AssumeUniversal | AdjustToUniversal`).

- Cadence alignment guard.
  - Rejects timestamps not aligned to pipeline cadence (`PipelineConstants.MinutesPerStep`, currently 15 min) or with non-zero seconds.

- Nullable numeric parsing for raw fields with Swedish decimal culture.
  - Empty cells are treated as missing values (`null`).

- Non-finite value rejection for ingested numerics.
  - NaN/Infinity are rejected in `ParseNullableDouble(...)`.

## 3) Deduplication and ordering safeguards

- Keep-last deduplication by timestamp.
  - `CollectionHelpers.DeduplicateByKeyKeepLast(...)`
  - Used in both Part 1 and Part 2 to remove duplicate timestamps deterministically.

- Deterministic ordering after dedup.
  - Deduplicated data is ordered by key (timestamp), stabilizing downstream behavior.

## 4) Missing-data handling safeguards

- Forward-fill for required series with hard failure when no observed values exist.
  - `ForwardFillWithImputationFlags(...)` throws when a required series has zero observed values.

- Validation bootstrap guard.
  - `EnsureObservedBeforeValidation(...)` enforces that each key series (target + exogenous variables) has at least one observed value before validation start.

- Imputation provenance tracking.
  - `FilledValue` tracks `IsImputed` and whether imputation came from a prior segment.

## 5) Leakage controls in preprocessing (Part 1)

- Segment-aware preprocessing.
  - Data is split into train/validation segments before target imputation logic is finalized for persisted outputs.

- Training-to-validation carryover purge.
  - Validation rows whose target is imputed from training context are dropped from persisted dataset.

- Validation-to-validation imputation preserved.
  - Validation rows imputed from prior validation observations are kept.

- Audit trail for dropped validation rows.
  - `PreprocessingAuditEvent` records dropped rows with reason and source (`ImputationSource = "Training"`).

## 6) Leakage controls in supervised dataset creation (Part 2)

- Fail-fast regular-cadence guard before lag/horizon mapping.
  - Part 2 now validates consecutive timestamps are exactly `PipelineConstants.MinutesPerStep` apart.
  - Any gap/irregular interval throws `InvalidOperationException` with offending timestamp pair and observed interval.
  - Rationale: Part 2 contracts (`Lag192`, `t+1..t+192`) are step-based and require fixed cadence to remain time-correct.

- Lookback gate before feature/horizon construction.
  - Dataset generation only proceeds when enough history exists for lags and rolling windows.

- Horizon-aware train/validation split.
  - Train eligibility is based on full horizon end (`y(t+1..t+H)`), not just anchor timestamp.

- Boundary purge.
  - Anchors that are neither safe train nor validation are purged to avoid cross-split label leakage.

- Split accounting summary.
  - Summary captures candidate/train/validation/purged counts and output rows for traceability.

## 7) Operational artifact robustness

- Parent-directory creation before writes.
  - CSV/JSON writers ensure target directories exist.

- Preprocessing diagnostics emitted as artifacts.
  - Audit CSV + summary JSON support post-run validation and debugging.

## 8) Automated verification coverage

Key tests are in:

- `tests/Forecasting.App.Tests/CsvParsingTests.cs`
- `tests/Forecasting.App.Tests/Part1PreprocessingTests.cs`
- `tests/Forecasting.App.Tests/Part2FeatureEngineeringTests.cs`

These cover:

- parsing failures and message quality
- deduplication behavior
- forward-fill behavior and guardrails
- leakage boundary purges in Part 1 and Part 2
- split/purge accounting and boundary expectations
