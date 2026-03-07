# Architecture Overview

## Scope

This document describes the current implementation architecture for:

- Part 1 preprocessing and leakage-safe evaluation preparation
- Part 2 feature engineering and multi-step supervised dataset generation
- Part 3 forecasting (baseline + ML.NET FastTree recursive inference)

The solution is implemented in .NET 10 under `src/Forecasting.App` and verified with tests in `tests/Forecasting.App.Tests`.

## Module Structure

- `Program.cs`
	- CLI entrypoint.
	- Routes execution to:
		- Part 1 default flow (raw CSV + holidays -> preprocessed feature matrix + audit artifacts)
		- Part 2 flow (`part2` mode) (Part 1 matrix -> supervised matrix + summary)
- `Part1Preprocessing.cs`
	- Functional core for Part 1 data ingestion, forward-fill, feature generation, split-first preprocessing, and compact auditing.
	- Key outputs:
		- `FeatureRow` matrix (persisted dataset)
		- `PreprocessingAuditEvent` CSV (event-level)
		- `PreprocessingAuditSummary` JSON
- `Part2FeatureEngineering.cs`
	- Functional core for Part 2 lag features, rolling statistics, and multi-step label construction (`t+1..t+192`).
	- Key outputs:
		- `Part2SupervisedRow` matrix
		- `Part2DatasetSummary` JSON
- `Part3Modeling.cs`
	- Functional core for Part 3 model training and validation forecasting.
	- Uses a model plug-in seam (`IForecastingModel`) and registry-driven execution.
	- Models:
		- Seasonal baseline (`BaselineSeasonal`)
		- ML.NET FastTree recursive one-step model (`FastTreeRecursive`)
	- Optional capabilities:
		- `IPermutationImportanceModel` for models that can provide PFI metrics.
	- Key outputs:
		- `Part3ForecastRow` prediction matrix
		- `Part3RunSummary` JSON
	- Runtime optimization:
		- Indexed history lookups + incremental rolling windows for recursive inference (see ADR 002).
- `tests/Forecasting.App.Tests`
	- Unit tests for Part 1 and Part 2 behavior, including split/purge boundaries and artifact writing paths.

## Data Flow

1. Raw ingestion (Part 1)
	 - Input: `data/testdata.csv`, `data/holidays.public.csv`
	 - Build typed rows, deduplicate by `utcTime` (keep-last), impute required numeric values, and generate calendar/cyclical features.
2. Split-first evaluation preprocessing (Part 1)
	 - Validation starts at last timestamp minus 30 days.
	 - Forward-fill is applied per segment to avoid cross-boundary target leakage.
	 - Validation rows with target imputed from training context are excluded from persisted dataset.
	 - Persist feature matrix and compact audit artifacts.
3. Supervised dataset assembly (Part 2)
	 - Input: Part 1 feature matrix CSV.
	 - Deduplicate again (defensive keep-last), compute lag/rolling features causally, build full 192-step horizon labels.
	 - Apply anchor split/purge rules to prevent train/validation leakage for multi-step targets.
	 - Persist supervised matrix and summary JSON.
4. Forecast modeling and inference (Part 3)
	 - Input: Part 2 supervised matrix CSV.
	 - Train on `Split=Train`, forecast on `Split=Validation`.
	 - Produce full 192-step horizon predictions for both models.
	 - For recursive FastTree inference, use indexed history + incremental rolling statistics to keep per-step feature updates efficient.
	 - Persist predictions CSV and run summary JSON.

## Leakage Controls

- Part 1:
	- Split-first preprocessing with explicit validation boundary.
	- Filtering of validation rows whose target value was imputed from training context.
- Part 2:
	- Feature windows are causal (history up to anchor time only).
	- Split eligibility considers horizon end for train anchors.
	- Boundary anchors are purged when future labels would overlap validation.
- Part 3:
	- Training uses only `Split=Train` anchors.
	- Validation inference is recursive and uses only historical/previously predicted target values at each step.

## Runtime Artifacts

- Part 1 artifacts (default):
	- `artifacts/part1_feature_matrix.csv`
	- `artifacts/part1_feature_matrix.audit.csv`
	- `artifacts/part1_feature_matrix.audit.summary.json`
- Part 2 artifacts (`part2` mode):
	- `artifacts/part2_supervised_matrix.csv`
	- `artifacts/part2_supervised_matrix.summary.json`
- Part 3 artifacts (`part3` mode):
	- `artifacts/part3_predictions.csv`
	- `artifacts/part3_predictions.summary.json`

## Adding a Part 3 Model

Use this flow to add a new forecasting model without changing central orchestration logic.

1. Implement the model contract in `src/Forecasting.App/Part3Modeling.cs`:
	- Add a class implementing `IForecastingModel` with:
		- `ModelName` (stable output name written to artifacts)
		- `Train(IReadOnlyList<Part3InputRow> trainRows, IReadOnlyList<Part3InputRow> allRows)`
		- `Predict(Part3InputRow anchor)` returning `(Predictions, FallbackSteps)`
2. If the model supports permutation feature importance, also implement `IPermutationImportanceModel` and return `Part3PfiResult` from `ComputePermutationImportance(...)`.
3. Register the model in `CreateModelRegistry(...)` in `src/Forecasting.App/Part3Modeling.cs`.
4. Keep output contract compatibility:
	- Return exactly `PipelineConstants.HorizonSteps` predictions.
	- Use deterministic `ModelName` (used by Part 4 and diagnostics grouping).
	- Ensure fallback counter semantics are explicit and deterministic.
5. Add tests in `tests/Forecasting.App.Tests/Part3ModelingTests.cs`:
	- Model appears in `Part3RunSummary.Models`.
	- Forecast rows are emitted for both `Train` and `Validation` anchors.
	- Predictions are finite and horizon length is correct.
	- Any model-specific fallback behavior is validated.

Because `RunModels(...)` iterates over the registry, adding a model should only require model class + registry + tests, not orchestration rewrites.

## Architectural Style

- Functional core / imperative shell:
	- Core transformations are implemented as pure/static methods over typed records where practical.
	- File I/O and CLI concerns remain at the outer edge (`Program.cs`, CSV/JSON writers).
- Deterministic preprocessing:
	- Duplicate handling, split rules, and persisted summaries are explicit and reproducible.
