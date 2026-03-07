# FOLLOW_UPS.md

## Part 1 follow-up

- During Part 1 implementation, preprocessing was extended to forward-fill additional numeric columns (`Temperature`, `Windspeed`, `SolarIrradiation`) in addition to `Target` to tolerate sparse rows in the provided dataset.
- Follow-up action: validate this imputation strategy with stakeholders and decide whether these columns should remain forward-filled, become configurable per column, or use a different strategy.
- DONE (2026-03-05): persist the generated Part 1 feature matrix to the `artifacts/` folder as CSV so output is inspectable and reusable across later phases.
- Follow-up action: possibly persist a preprocessing lineage file to `artifacts/` (for example transformation steps, fill strategy, and input/output metadata) for traceability.
- DONE (2026-03-05): implement split-first preprocessing for evaluation by hard-coding validation as the last 30 days, then applying imputations after the split to avoid cross-boundary leakage.
- DONE (2026-03-05): as part of preprocessing, exclude validation rows where `Target` is imputed from training context before persisting the dataset sent downstream, while keeping validation rows imputed from prior validation rows; persist compact audit artifacts (event-only CSV for dropped rows + summary JSON) so filtering is reproducible and inspectable.
- Follow-up action: add and enforce a boundary check that each required column’s first observed non-null value occurs before validation starts; fail fast or warn explicitly if this is not true.
- Follow-up action: add a lightweight preprocessing QA visualization step (time-series + distribution/boxplot for key numeric features, including imputed segments) to detect outliers, flatlines, clipping, or other suspicious patterns before downstream modeling.
- DONE (2026-03-05): add explicit timestamp deduplication in preprocessing using deterministic keep-last by `utcTime`, with audit/summary count (`DroppedDuplicateTimestampRows`) for removed duplicates.
- Follow-up action: add missing/irregular interval diagnostics for `utcTime` (gap-size profiling and unexpected cadence detection) and persist counts in preprocessing summary artifacts.
- Follow-up action: add explicit timestamp monotonicity validation (fail fast on non-monotonic/out-of-order rows before downstream feature generation).
- Follow-up action: harden timestamp parsing/normalization checks for UTC consistency (including timezone/DST edge cases when source inputs are ambiguous).
- Follow-up action: add column-level domain/range validation rules (for example physically impossible negatives or implausible spikes) with clear policy per column (flag/clip/drop).
- Follow-up action: add stale-sensor detection (long constant runs/flatlines) for numeric telemetry columns and surface this in audit summaries.
- Follow-up action: add target-quality safeguards for evaluation (for example explicit handling/reporting for zero or near-zero targets in percentage-based metrics).

## Part 2 follow-up

- DONE (2026-03-06): increase branch coverage for `Part2FeatureEngineering` to meet the repository target; latest coverage run reports branch-rate ~93.18% for `Part2FeatureEngineering`.
- DONE (2026-03-06): add targeted Part 2 tests for split-boundary behavior, including explicit train-anchor coverage and deterministic assertions around the purge zone near validation start.
- Follow-up action: add explicit continuity validation for Part 2 input cadence (expected 15-minute step); fail fast or clearly report when timestamp gaps/irregular intervals would break index-based lag/horizon semantics.
- Follow-up action: remove or repurpose currently unused `MinutesPerStep` constant in Part 2 implementation to keep the module intentional and warning-free.
- Follow-up action: optimize rolling-stat feature computation for lagged targets (`TargetLag192*` and `TargetLag672*`) by replacing per-anchor full-window recomputes (mean/std rescans over 16/96 windows) with incremental rolling sums/sums-of-squares to reduce CPU cost in the Part 2 anchor loop.

## Part 3 follow-up

- DONE (2026-03-06): raise branch coverage for `Part3Modeling` to repository target; latest coverage run reports branch-rate ~92.64% for `Part3Modeling` with targeted tests for parsing failures, split guards, and fallback branches.
- DONE (2026-03-06): optimize recursive inference lookups by replacing repeated LINQ scans in `GetValueAtOrBefore` with indexed history state and binary-search lookup.
- DONE (2026-03-06): optimize rolling feature computation by maintaining incremental rolling sums / sums-of-squares for 16/96-step windows.
- DONE (2026-03-06): remove unused `allRows` parameter from `PredictWithFastTreeRecursive` and related dead paths.
- Follow-up action: clarify fallback counters/labels so baseline fallback reporting is not named `ExogenousFallbackSteps` (separate seasonal-key fallback vs exogenous fallback semantics).
- Follow-up action: persist Part 3 fallback telemetry in artifacts (for example non-finite score fallback to `targetAtT`, exogenous carry-forward fallback, and lag/lookup fallback counts) so users can audit how often safeguards were applied.
- Follow-up action: split Part 3 summary fallback counters by model semantics (for example `SeasonalKeyFallbackSteps` for baseline and `ExogenousCarryForwardFallbackSteps` / `NonFiniteScoreFallbackSteps` for FastTree) instead of a shared `ExogenousFallbackSteps` label.
- DONE (2026-03-07): persist Part 3 FastTree feature-importance scores based on out-of-sample performance (for example holdout/backtest permutation feature importance, not training-fit-only importance) to an artifact so feature influence is inspectable. Planned in `docs/CASE_EXECUTION_PLAN.md` under "Permutation Feature Importance" section.
- Permutation importance:
- DONE (2026-03-07): gate permutation-importance execution behind an explicit CLI flag (`--pfi`) so default runs remain fast and deterministic unless the analysis is explicitly requested.
- DONE (2026-03-07): add optional CLI horizon selection for permutation-importance analysis via `--pfi-horizon` (1..192), so feature relevance can be inspected for specific forecast steps (for example day-ahead and two-day-ahead slices) instead of only `t+1`.
- Follow-up action: consider streaming prediction writes for large validation sets to avoid retaining all forecast rows in memory before CSV persistence.
- DONE (2026-03-07): persist Part 3 predictions for both `Split=Train` and `Split=Validation` so diagnostics can report in-sample vs out-of-sample errors from a single prediction artifact while keeping Part 4 metrics validation-only.

## Part 4 follow-up

- DONE (2026-03-06): harden Part 4 evaluation guards by failing fast when a predicted model has zero evaluable validation points after key alignment.
- DONE (2026-03-06): reject non-finite prediction values (`NaN`/`Infinity`) during CSV parsing to prevent poisoned metrics.
- DONE (2026-03-06): make 48h sample selection deterministic on the earliest anchor with matched actuals (not merely earliest prediction anchor).

## Diagnostics & visualization follow-up

- DONE (2026-03-06): add a standalone diagnostics command (separate from core `part3`/`part4`) that generates lightweight visualization artifacts for quick inspection.
- DONE (2026-03-06): include pre-model diagnostics (target level/trend shift checks, train-vs-validation distribution comparison, and cadence/missingness summaries).
- DONE (2026-03-06): include post-model diagnostics (predicted-vs-actual plots for sampled anchors, residual distribution, and signed-bias by horizon bucket).
- DONE (2026-03-06): persist diagnostics outputs as versionable artifacts (CSV summaries + simple HTML report) under `artifacts/diagnostics/`.
- Follow-up action: document and surface the observed long-season pattern in target behavior (lower summers, higher winters) in diagnostics output and/or summary notes so model interpretation is explicit.
- Follow-up action: add a simpler one-command entrypoint/task for running the Part 1 pipeline and writing its artifacts, so the first phase is easy to execute repeatedly.
- Follow-up action: add a second FastTree variant that includes month-of-year seasonality features (for example month cyclic encoding) and evaluate it side-by-side with the current FastTree feature set; keep the current FastTree path unchanged/default so existing runs remain reproducible.
