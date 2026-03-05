# FOLLOW_UPS.md

## Part 1 follow-up

- During Part 1 implementation, preprocessing was extended to forward-fill additional numeric columns (`Temperature`, `Windspeed`, `SolarIrradiation`) in addition to `Target` to tolerate sparse rows in the provided dataset.
- Follow-up action: validate this imputation strategy with stakeholders and decide whether these columns should remain forward-filled, become configurable per column, or use a different strategy.
- DONE (2026-03-05): persist the generated Part 1 feature matrix to the `artifacts/` folder as CSV so output is inspectable and reusable across later phases.
- Follow-up action: possibly persist a preprocessing lineage file to `artifacts/` (for example transformation steps, fill strategy, and input/output metadata) for traceability.
- Follow-up action: implement split-first preprocessing for evaluation by hard-coding validation as the last 30 days, then applying imputations after the split to avoid cross-boundary leakage.
- Follow-up action: as part of the preprocessing routine, exclude validation rows where `Target` is imputed from training context before persisting the preprocessed dataset that is sent to downstream steps, while keeping validation rows imputed from prior validation rows; persist a per-row audit artifact (for example flags like `IsValidation`, `IsTargetImputed`) so filtering is reproducible and inspectable.
- Follow-up action: add and enforce a boundary check that each required column’s first observed non-null value occurs before validation starts; fail fast or warn explicitly if this is not true.
