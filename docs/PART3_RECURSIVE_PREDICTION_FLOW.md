# Part 3 Recursive Prediction Flow

This document explains how recursive prediction works in Part 3 and what each major piece of the implementation is responsible for.

Scope:

- `src/Forecasting.App/Part3Modeling.cs`
- the `FastTreeRecursive` prediction path
- the shared recursive rollout used by both production inference and oracle-style tests

## Why this flow exists

The FastTree model is trained as a one-step model, but Part 3 needs a full 192-step forecast horizon.

That means prediction cannot be done in one call returning `t+1..t+192` directly. Instead, the code must:

1. predict the next step
2. feed that predicted value back into the history
3. rebuild the next step's features using that updated history
4. repeat until the full horizon is produced

This is the recursive forecasting loop.

## High-level structure

The Part 3 FastTree path is intentionally split into two concerns:

1. Recursive rollout mechanics
   - handled by `PredictRecursively(...)`
   - owns time stepping, context lookup, lag/history reads, rolling-stat updates, and feeding predictions back into history

2. One-step scoring
   - provided by a `scorer`
   - in production this scorer calls the ML.NET `PredictionEngine`
   - in tests this scorer can be a simple deterministic function

This split makes the recursive logic directly testable without needing an oracle for FastTree internals.

## Main pieces and responsibilities

### `BuildFastTreeRecursiveModel(...)`

This method prepares everything needed for recursive inference.

It does three things:

1. trains the one-step FastTree model on training rows
2. creates the ML.NET prediction engine for one-step scoring
3. builds the historical lookup structures used during recursive inference

The historical structures are:

- `RowByTimestamp`
  - a timestamp-to-row lookup used to fetch exogenous context for a given step
- `HistoryTimestamps`
  - sorted timestamps for historical target access
- `HistoryValues`
  - aligned target values for those timestamps

These are stored in `FastTreeRecursiveModel` and reused across predictions.

### `BuildRecursiveHistory(...)`

This method constructs the history view used by recursive prediction.

It:

1. orders all rows by anchor timestamp
2. builds a lookup by timestamp
3. deduplicates repeated timestamps using keep-last semantics
4. extracts parallel arrays for timestamp-based history access

This is done once so the prediction loop does not repeatedly rebuild history state.

### `PredictWithFastTreeRecursive(...)`

This is the production entrypoint for FastTree recursive prediction.

It does not implement the recursive loop itself. Instead it:

1. prepares a reusable feature buffer and reusable ML.NET input row
2. defines the production scorer
3. passes that scorer into `PredictRecursively(...)`

The scorer is simply: given the current `FeatureSnapshot`, turn it into a feature vector, call ML.NET, and return one predicted value.

### `PredictWithRecursiveOracle(...)`

This is the test seam.

It uses the same recursive rollout as production, but instead of using ML.NET as the scorer, tests can pass a simple deterministic function such as:

- `snapshot => snapshot.TargetAtT + snapshot.Temperature`
- `snapshot => snapshot.Temperature`

That allows exact oracle-style tests of the recursive mechanics.

### `PredictRecursively(...)`

This method is the core of the recursive forecasting logic.

It owns:

1. stepping through the horizon one step at a time
2. selecting exogenous context for the current step
3. reading target history in a leakage-safe way
4. updating rolling windows
5. building the current feature snapshot
6. calling the scorer for one predicted value
7. appending that prediction back into history for later steps

This is the key method to understand when debugging Part 3 behavior.

### `RecursiveHistoryState`

This class gives the prediction loop a history view that is truncated at the anchor time and then extended with predictions.

It contains:

- base history from real rows up to the anchor
- predicted timestamps and values generated during the current rollout

Its main purpose is to ensure later steps can see earlier predictions, but cannot see future actual target values beyond the anchor.

### `RollingWindowStats`

This class maintains rolling means and standard deviations incrementally.

Instead of recomputing each rolling statistic from scratch for every step, it updates the window by removing one value and adding one value.

That keeps recursive inference much cheaper than recalculating all window statistics each time.

## Step-by-step prediction flow for one anchor

The following describes what happens when Part 3 predicts the full horizon for a single anchor row.

### Step 0: Start from the anchor

Input:

- one `Part2SupervisedRow` anchor
- model history structures built earlier
- either the production ML.NET scorer or a test scorer

The anchor provides the starting point for target, exogenous values, lag features, and fallback defaults.

### Step 1: Create a history view truncated at the anchor

`PredictRecursively(...)` computes the final base-history index at or before the anchor time and creates a `RecursiveHistoryState`.

At this point:

- past data is visible
- future actual targets are not visible
- predicted values list is empty

This is what prevents target leakage during recursive inference.

### Step 2: Initialize rolling windows

The method initializes rolling statistics for the lag-192 feature family.

These windows start from history values available at the anchor and are then incrementally updated as the recursive loop advances.

### Step 3: Seed fallback exogenous values

The latest known values from the anchor are stored for:

- temperature
- windspeed
- solar irradiation

These values are frozen for the recursive horizon in the current implementation.
This avoids using realized future exogenous observations during validation forecasting.

### Step 4: Enter the recursive horizon loop

For each step from 1 to 192:

#### 4.1 Compute the current feature timestamp

The loop uses a `currentTime` representing the feature timestamp `t` used to predict the next point.

For the first iteration:

- `currentTime = anchor time`

For later iterations:

- `currentTime` advances one cadence step each time

#### 4.2 Fetch exogenous context

The code distinguishes between two kinds of future context:

1. calendar-derived context that is valid to know in advance
  - for example `IsHoliday`

2. realized exogenous measurements that are not valid to look up in the future during evaluation
  - `Temperature`
  - `Windspeed`
  - `SolarIrradiation`

For that reason, the recursive loop:

- keeps `Temperature`, `Windspeed`, and `SolarIrradiation` fixed at their anchor-time values
- still allows holiday/calendar-derived context to advance when it is known from the timestamp/calendar

For steps after the anchor:

- `fallbackSteps` is incremented because future realized exogenous measurements are intentionally not used

This keeps the recursive loop target-leakage safe and also avoids exogenous lookahead from realized future weather variables.

#### 4.3 Read target history causally

The code reads:

- `targetAtT`
- `targetLag192`
- `targetLag672`

from `RecursiveHistoryState`.

This is the crucial recursive behavior:

- before predictions begin, history reads come from real past values
- after predictions begin, later steps can read earlier predicted values

So the loop progressively transitions from observed history to predicted history.

#### 4.4 Update rolling windows

Once the first step has passed, the lag-192 rolling windows are updated with the current lagged value.

This keeps rolling means/std values aligned with the same evolving history seen by the rest of the features.

#### 4.5 Compute calendar and cyclic features

The loop derives:

- hour
- minute
- day of week
- cyclic encodings from `CyclicLookup`

These are deterministic functions of `currentTime`.

#### 4.6 Build the feature snapshot

The code constructs a `FeatureSnapshot` containing all features needed for one-step scoring.

This snapshot combines:

- autoregressive information from history
- anchor-frozen exogenous values for temperature/wind/solar
- calendar-derived context for the current step
- calendar/cyclic features
- rolling statistics
- selected anchored lag-672 rolling features that remain fixed in the current implementation

#### 4.7 Score the snapshot

The snapshot is passed to the `scorer`.

In production:

1. `FillFeatureVector(...)` writes the snapshot into the reusable float buffer
2. ML.NET `PredictionEngine.Predict(...)` computes a score
3. non-finite scores fall back to `snapshot.TargetAtT`

In tests:

- the scorer may simply perform a small arithmetic expression over snapshot fields

#### 4.8 Store the prediction in the output horizon

The predicted value is written into the output array at index `step - 1`.

#### 4.9 Feed the prediction back into history

The prediction is appended to `RecursiveHistoryState` at the next timestamp.

This is what makes the method recursive:

- step 2 can depend on step 1's prediction
- step 3 can depend on steps 1 and 2
- and so on across the full horizon

### Step 5: Return the full forecast

After 192 iterations, the method returns:

- `Predictions`: the full recursive horizon
- `FallbackSteps`: how many steps had to carry forward exogenous values due to missing context

## Why the scorer split matters

Without the scorer split, the recursive logic and ML.NET scoring would be fused together.

That would make exact oracle-style testing difficult because the test would need to know the exact internal output of FastTree.

With the split:

1. the production code still uses the real ML.NET scorer
2. tests can substitute a simple deterministic scorer
3. the recursive mechanics are tested directly, not approximated

This makes it possible to prove properties such as:

- later steps consume earlier predictions
- future realized exogenous values are not used beyond the anchor
- known calendar context can still advance with time

## How to debug this flow

The most useful breakpoint locations in `Part3Modeling.cs` are:

- start of `PredictRecursively(...)`
- exogenous context lookup
- `targetAtT` history read
- scorer call
- `history.AppendPredicted(...)`

If the goal is understanding the mechanics rather than the ML.NET model itself, debugging the oracle-style tests is usually easier than debugging a full app run.

## Summary

The Part 3 FastTree path is a recursive forecasting engine wrapped around a one-step scorer.

- `PredictRecursively(...)` owns the forecast mechanics
- the scorer owns the one-step prediction rule
- production uses ML.NET as the scorer
- tests use simple deterministic scorers

That split keeps the implementation efficient, keeps target leakage under control, and makes the recursive logic understandable and testable.