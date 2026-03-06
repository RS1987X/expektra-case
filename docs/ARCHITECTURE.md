# Architecture Overview

## Scope

This document describes the current implementation architecture for:

- Part 1 preprocessing and leakage-safe evaluation preparation
- Part 2 feature engineering and multi-step supervised dataset generation

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

## Leakage Controls

- Part 1:
	- Split-first preprocessing with explicit validation boundary.
	- Filtering of validation rows whose target value was imputed from training context.
- Part 2:
	- Feature windows are causal (history up to anchor time only).
	- Split eligibility considers horizon end for train anchors.
	- Boundary anchors are purged when future labels would overlap validation.

## Runtime Artifacts

- Part 1 artifacts (default):
	- `artifacts/part1_feature_matrix.csv`
	- `artifacts/part1_feature_matrix.audit.csv`
	- `artifacts/part1_feature_matrix.audit.summary.json`
- Part 2 artifacts (`part2` mode):
	- `artifacts/part2_supervised_matrix.csv`
	- `artifacts/part2_supervised_matrix.summary.json`

## Architectural Style

- Functional core / imperative shell:
	- Core transformations are implemented as pure/static methods over typed records where practical.
	- File I/O and CLI concerns remain at the outer edge (`Program.cs`, CSV/JSON writers).
- Deterministic preprocessing:
	- Duplicate handling, split rules, and persisted summaries are explicit and reproducible.
