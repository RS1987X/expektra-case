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
