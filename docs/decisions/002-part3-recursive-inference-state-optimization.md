# ADR 002: Part 3 recursive inference state optimization

- Status: Accepted
- Date: 2026-03-06

## Context

Part 3 forecasting uses recursive one-step inference for a 192-step horizon.

The initial implementation performed repeated scan/sort work during inference:

- value lookup at-or-before timestamp repeatedly scanned history
- rolling mean/std were recomputed from full windows at each step
- ML.NET prediction engine was recreated for each validation anchor

This increases runtime and memory pressure as the number of anchors grows.

## Decision

Adopt an indexed recursive state for inference:

- Keep immutable, timestamp-sorted historical targets in arrays and resolve at-or-before lookups with binary search.
- Maintain per-anchor predicted values as an append-only sequence layered over immutable history.
- Replace full rolling-window recomputation with incremental rolling statistics (running sum and sum-of-squares) for the 16-step and 96-step windows.
- Reuse a single ML.NET `PredictionEngine` instance across anchors in a run.

## Consequences

Positive:

- Reduces lookup complexity from repeated full scans to binary search over indexed data.
- Reduces rolling feature cost from repeated window materialization to O(1) updates per step.
- Removes repeated `PredictionEngine` construction overhead.
- Preserves current Part 3 behavior and output contract.

Trade-offs:

- Adds implementation complexity (stateful helper types for history and rolling windows).
- Prediction remains sequential per anchor; parallelization is still a future optimization.

## Alternatives Considered

1. Keep current implementation and rely on hardware scaling (rejected)
   - Does not address algorithmic hotspot costs.

2. Fully precompute all recursive features for all anchors (rejected)
   - Complicates control flow and increases memory usage.

3. Move to a direct multi-output model to avoid recursion (rejected for now)
   - Out of current phase scope and changes model contract significantly.
