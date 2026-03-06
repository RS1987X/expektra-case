# ADR 001: Part 2 anchor-based split eligibility and purge policy

- Status: Accepted
- Date: 2026-03-06

## Context

Part 2 creates supervised rows where each anchor time `t` maps to a full target vector `y(t+1..t+192)`.

If split assignment uses only anchor timestamp, training rows near the validation boundary can include future labels that overlap validation time. That creates cross-split leakage for multi-step supervision.

## Decision

Use anchor-based samples with horizon-aware split eligibility:

- Training anchor condition: `t + H < validationStart`
- Validation anchor condition: `t >= validationStart` and full horizon exists
- Purge boundary anchors that satisfy neither condition

Where `H = 192` (48 hours at 15-minute resolution).

Additional conventions:

- Keep full-horizon labels only (`t+1..t+192`), no partial horizon labels in Part 2.
- Compute lag and rolling features causally using values up to anchor time `t` only.

## Consequences

Positive:

- Prevents train/validation leakage from overlapping label horizons.
- Produces deterministic, explainable train/validation row assignment.
- Aligns implementation with documented Part 2 leakage-safe mapping rules.

Trade-offs:

- Reduces usable row count due to purge zone before validation start.
- Requires explicit summary counters (candidate, purged, train, validation) for auditability.

## Alternatives Considered

1. Split by anchor timestamp only (rejected)
   - Simpler but leaks future labels across split boundary.

2. Allow partial horizons near validation boundary (rejected for Part 2)
   - Increases sample count but complicates downstream modeling and evaluation consistency.

3. Dynamic horizon truncation per row (rejected for Part 2)
   - Adds complexity and inconsistent label shape; postponed unless later phases require it.
