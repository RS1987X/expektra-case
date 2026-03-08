# ADR 003: Eliminate ForecastPredictionPoint explosion in Part4/Diagnostics

- Status: Accepted
- Date: 2026-03-08

## Context

Part4Evaluation and PartDiagnostics consumed forecast data through an intermediate
`ForecastPredictionPoint` representation created by `PredictionPoints.BuildFromForecastRows()`.
This method exploded 147,650 `Part3ForecastRow` objects (each containing a 192-element
`PredictedTargets` array) into 28.3 million individual `ForecastPredictionPoint` records,
plus a 28.3M-entry `HashSet` for duplicate detection.

Called twice in `PipelineRunner.RunAll` (once for Part4, once for Diagnostics), this produced
~4.5 GB of heap pressure from short-lived small objects, causing intermittent OOM crashes
and heavy GC pauses.

## Decision

Eliminate the flat `ForecastPredictionPoint` representation entirely. All methods in
Part4Evaluation and PartDiagnostics now iterate `IReadOnlyList<Part3ForecastRow>` directly,
using an inner `for (step = 1; step <= 192; step++)` loop to access individual horizon
predictions via `forecast.PredictedTargets[step - 1]`.

Validation previously performed by `BuildFromForecastRows` (duplicate-key detection,
non-finite value rejection) was moved to `Part4Evaluation.ValidateForecastRows()`.

`PredictionPoints.cs` becomes dead code and is removed.

## Consequences

- Part4Evaluation runs with near-zero additional allocation beyond its accumulators.
- PartDiagnostics still allocates `RunningStats` residual lists for quantile computation,
  but avoids the 28.3M-object intermediate layer.
- The pipeline no longer crashes intermittently from OOM on standard workstations.
- If a new consumer needs flat per-step iteration, it should follow the same
  `foreach row / for step` pattern rather than re-introducing the explosion.
