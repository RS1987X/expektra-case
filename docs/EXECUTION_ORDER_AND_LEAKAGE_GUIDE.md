# Execution Order and Leakage-Safe Preprocessing Guide

This guide documents the practical execution order used in this repository and explains which preprocessing options can introduce data leakage if they are not applied fold-locally.

It complements:

- `docs/CASE_EXECUTION_PLAN.md`
- `docs/AFML_FORECASTING_WORKFLOW.md`

---

## 1) Canonical execution order

Use this order for offline model development and evaluation:

1. **Problem formulation**
   - Define target, forecast horizon, cadence, feature cutoff, and primary metric.
2. **EDA and diagnostics**
   - Quantify seasonality, anomalies, data-quality issues, and stationarity diagnostics.
3. **Preprocessing policy design**
   - Decide cleaning and transform policy (dedup/gap-fill/outlier/transform), including causal constraints.
4. **CV split design (time-aware)**
   - Define walk-forward splits with purging + embargo.
5. **Fold-local preprocessing execution**
   - For each fold: `Fit` on fold-train only, then `Apply` to fold-validation.
   - For final holdout: `Fit` on full pre-holdout train, then `Apply` to holdout.
6. **Modeling**
   - Train/forecast each baseline or candidate model using preprocessed fold data.
7. **Evaluation and model selection**
   - Aggregate fold metrics, rank models, report holdout metrics, and persist artifacts.

### Pre-split canonicalization vs fold-local preprocessing

To avoid confusion, this workflow distinguishes two categories:

- **Safe pre-split canonicalization (allowed before CV split):**
   - deterministic timestamp ordering,
   - schema/parse validation,
   - row-local validity checks that do not learn parameters from data distribution.
- **Fold-local preprocessing (must run after CV split):**
   - any learned/statistical transform (for example outlier thresholds, scaling/decomposition stats),
   - any boundary-crossing transform that can use future observations (for example interpolation across partitions).

`Sort by timestamp` is treated as canonicalization, not a learned transform, so it can be required before splitting without introducing leakage.

### EDA decision boundary (reportable mode)

If EDA outputs are used to make modeling/preprocessing decisions, run EDA on **pre-holdout data only**.

- Keep one final holdout window untouched for final evaluation.
- Build CV folds only on the pre-holdout development window.
- Use the same holdout-step definition for both EDA and evaluation (single source of truth) so boundaries are consistent across tools.

### Why step 5 comes after step 4

If you execute split-sensitive preprocessing globally before splits are defined, parameters learned from future periods can leak into earlier periods. This inflates offline performance and breaks the intended out-of-sample simulation.

---

## 2) How this repository implements that order

- Split generation and evaluation orchestration:
  - `src/Forecasting.Core/Evaluation/BaselineEvaluationPipeline.cs`
- Purging/embargo and time-safe train/validation index construction:
  - `src/Forecasting.Core/Evaluation/PurgedWalkForwardSplitter.cs`
- Fold-local preprocessing state (`Fit`/`Apply`):
  - `src/Forecasting.Core/Preprocessing/TimeSeriesPreprocessor.cs`
  - `src/Forecasting.Core/Preprocessing/TimeSeriesPreprocessorFitState.cs`
- Regression tests enforcing leakage-safe behavior:
  - `tests/Forecasting.Core.Tests/BaselineEvaluationPipelineTests.cs`
  - `tests/Forecasting.Core.Tests/PurgedWalkForwardSplitterTests.cs`

---

## 3) Leakage-prone preprocessing options (and why)

Below are the options that can leak information if executed globally instead of fold-locally.

### A) Outlier handling options (high risk)

Relevant options:

- `OutlierHandlingStrategy` (except `None`)
- `OutlierIqrMultiplier`

Why leakage can occur:

- Outlier bounds are learned from data distribution (IQR-based bounds).
- If fitted on the full series, validation/holdout values influence thresholds used on train or validation processing.

Safe pattern:

- Per fold: `Fit(train)` -> persist bounds in `TimeSeriesPreprocessorFitState` -> `Apply(train/validation)`.

Notes by strategy:

- `ClipWinsorize`: threshold leakage risk (distribution learned from future if global fit).
- `ForwardFillFromLastValid`: replacement is causal, but outlier detection still depends on fitted bounds.
- `LocalAverage`: same threshold risk; plus optional forward-neighbor risk if lookahead > 0.

### B) Gap fill with linear interpolation (medium/high risk)

Relevant option:

- `GapFillStrategy.LinearInterpolation`

Why leakage can occur:

- Interpolation uses both previous and next observed points.
- If preprocessing is global before split, a train-period gap can be filled using a validation-period future point.

Safe pattern:

- Apply interpolation only within each partition boundary (train separately from validation/holdout), never across boundary.

### C) Local-average outlier replacement with lookahead (high risk)

Relevant options:

- `OutlierHandlingStrategy.LocalAverage`
- `LocalAverageLookaheadSteps` (when > 0)

Why leakage can occur:

- Replacement can explicitly use future neighbors.
- If partition boundaries are not respected, information from validation/holdout can leak into train adjustments.

Safe pattern:

- Keep `LocalAverageLookaheadSteps = 0` for strictly causal inference paths.
- For offline diagnostics where lookahead is allowed, enforce strict partition-local execution.

### D) Quality-threshold degraded-mode decision (context risk)

Relevant options:

- `Thresholds.MaxDuplicateRate`
- `Thresholds.MaxMissingRate`
- `Thresholds.MaxOutlierRate`

Why leakage can occur:

- Degraded/not-degraded decisions are computed from observed rates.
- If rates are computed globally, early-period decisions can be influenced by later-period anomalies.

Safe pattern:

- Compute quality rates per partition/fold, not once globally.

---

## 4) Options that are generally low leakage risk

These are typically row-local or deterministic with respect to observed records and are not parameter-fit sensitive by themselves:

- `RequireUtcTimestamps`
- `RejectNonFinite`
- `EnforceNonNegative`

`DeduplicationPolicy` is usually low risk, but still should run inside each fold pipeline to keep fold reports and behavior partition-consistent.

---

## 5) Practical checklist before trusting metrics

Use this quick checklist for any new model/preprocessing experiment:

1. Splits are generated first (walk-forward + purging + embargo).
2. Any learned preprocessing parameter is fit on fold-train only.
3. Validation/holdout preprocessing uses `Apply` with train-fitted state.
4. No preprocessing step crosses train/validation/holdout boundary.
5. Fold indices and metrics artifacts are persisted and auditable.
6. Determinism + leakage tests pass.

---

## 6) Recommended defaults for this repo

- For API inference (strictly causal):
  - Avoid future-aware settings (`LinearInterpolation` across boundaries, `LocalAverageLookaheadSteps > 0`).
- For offline evaluation:
  - Use fold-local `Fit`/`Apply` always.
  - Keep purging + embargo enabled.
  - Treat any unexpectedly strong score as a leakage probe trigger.

## 7) Reportable evaluation runner

- Run reportable EDA first to emit `reportable_eda_metadata.json` and `reportable_decision.template.json`.
- Configure split policy during reportable EDA (for example `--target-folds <N>`), which is persisted in metadata and enforced by evaluation.
- Human approval step: copy/fill `reportable_decision.json` from the template and set `seasonalityMode` (`auto`, `explicit`, or `none`).
- Use `dotnet run --project tools/EvaluationRunner/EvaluationRunner.csproj -- --input <csv> --output-dir artifacts/evaluation/reportable --decision-artifact <path-to-reportable_decision.json>`.
- The runner enforces split-first ordering, fold-local preprocessing fits, boundary-safe preprocessing options, and required artifacts (`fold_indices.json`, `metrics_summary.json`, `regime_report.json`).
- It fails fast when the series is unsorted, when folds cannot be built, when interpolation/lookahead options would cross boundaries, when decision/metadata hashes mismatch, or when evaluation input hash differs from approved EDA input.
