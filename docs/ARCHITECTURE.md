# Architecture Overview

## System at a Glance

This repo implements a small forecasting pipeline with these modes:

- `part1`: preprocess raw data
- `part2`: build supervised forecasting rows
- `part3`: train models and generate forecasts
- `part4`: evaluate validation forecasts
- `diagnostics`: generate inspection artifacts and HTML diagnostics
- `all`: run the full pipeline end to end for Part 1 through Part 4

The code lives in `src/Forecasting.App` and is verified by tests in `tests/Forecasting.App.Tests`.

## Main Flow

1. Part 1 reads raw time-series data and holiday data.
2. Part 1 cleans the data, imputes missing values, and adds calendar/cyclical features.
3. Part 2 converts the time series into supervised rows: one anchor at time `t` with labels `t+1..t+192`.
4. Part 3 trains forecasting models and produces 192-step forecasts.
5. Part 4 evaluates validation forecasts with `MAE`, `RMSE`, and `MAPE`.
6. Diagnostics is an explicit, separate mode that writes extra summaries, plots, and HTML output for inspection.

## Main Modules

- `Program.cs`
  - CLI entrypoint.
  - Dispatches to `part1`, `part2`, `part3`, `part4`, `diagnostics`, or `all`.

- `Part1Preprocessing.cs`
  - Loads raw CSV input.
  - Deduplicates timestamps with keep-last behavior.
  - Imputes required numeric values.
  - Adds calendar and cyclical features.
  - Applies split-first preprocessing rules to avoid leakage.

- `Part2FeatureEngineering.cs`
  - Builds lag features and rolling statistics.
  - Builds one supervised row per valid anchor.
  - Produces 192-step target vectors.
  - Applies split/purge rules for leakage-safe training and validation.

- `Part3Modeling.cs`
  - Trains models and runs forecasting.
  - Contains the baseline model and FastTree recursive model.
  - Owns recursive inference logic, feature projection, and optional PFI.

- `Part4Evaluation.cs`
  - Evaluates validation forecasts only.
  - Computes `MAE`, `RMSE`, and `MAPE`.
  - Produces a deterministic sample of forecast-vs-actual points.

- `PartDiagnostics.cs`
  - Produces pre-model and post-model diagnostics.
  - Writes CSV, SVG, and HTML artifacts for inspection.

## Key Design Choices

These are the main choices that shape the current program.

### 1. Fixed forecasting contract

- The pipeline always predicts exactly `192` future steps.
- Each forecast is a full ordered vector `t+1..t+192`.
- Parts 2, 3, and 4 all use the same contract.

### 2. Time-based, leakage-safe workflow

- Data is treated as an ordered time series, not shuffled samples.
- Features are causal.
- Training uses only `Split=Train` rows.
- Evaluation uses validation forecasts only.

### 3. Registry-driven Part 3 models

- `IForecastingModel` is the extension seam for forecasting models.
- `CreateModelRegistry(...)` is the single registration point for models.
- `RunModels(...)` does not branch on model names.

This makes it easy to add more models without rewriting orchestration.

### 4. Centralized feature projection

- `FeatureSnapshot` is the full recursive-state object.
- `FeatureSchema` decides which fields are actually used by the ML model.
- The same schema controls feature vector order, exported feature names, and PFI slot names.

This keeps training, inference, and feature-importance analysis aligned.

### 5. Recursive engine separated from scorer logic

- `PredictRecursively(...)` owns the recursive mechanics:
  - step timing
  - calendar lookup
  - target-history lookup
  - lag updates
  - rolling-window updates
  - feeding predictions back into history
- Production FastTree scoring plugs into that engine through a scorer callback.
- Tests use the same engine with deterministic oracle scorers.

This is the main reason the recursive logic is easy to test without depending on ML.NET internals.

### 6. Explicit recursive state

- `RecursiveHistoryState` manages truncated history plus appended predictions.
- `RollingWindowStats` manages incremental rolling mean/std updates.

This keeps recursive state local and makes leakage boundaries easier to reason about.

### 7. Two-model comparison setup

- `BaselineSeasonal` is a deterministic reference model.
- `FastTreeRecursive` is the ML model.
- Both emit the same 192-step output shape so Part 4 can compare them directly.

## Current Part 3 Behavior

The current recursive forecasting behavior is:

- target-derived features update recursively from observed/predicted history
- calendar features roll forward with forecast time
- holiday context may roll forward since known from timestamp context
- exogenous weather features are frozen at the anchor during recursion
- non-finite ML scores fall back to the current recursive target value

## Runtime Artifacts

- Part 1:
  - `artifacts/part1_feature_matrix.csv`
  - `artifacts/part1_feature_matrix.audit.csv`
  - `artifacts/part1_feature_matrix.audit.summary.json`

- Part 2:
  - `artifacts/part2_supervised_matrix.csv`
  - `artifacts/part2_supervised_matrix.summary.json`

- Part 3:
  - `artifacts/part3_predictions.csv`
  - `artifacts/part3_predictions.summary.json`
  - optional PFI artifacts

- Part 4:
  - evaluation artifacts under `artifacts/`

- Diagnostics:
  - CSV, SVG, and HTML outputs under `artifacts/diagnostics/`

## When to Use an ADR

Use `docs/decisions/*.md` when a design choice has meaningful alternatives and you want a stable record of why one option was chosen.

Examples:

- choosing recursive vs direct forecasting
- choosing a new exogenous-input policy
- choosing a new modeling architecture
- choosing a different split or purge strategy

Use this architecture document for the current structure of the system and the main extension/testability patterns.
