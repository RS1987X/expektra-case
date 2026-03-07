# Refactor Plan: Orchestration, Model Extensibility, and Feature Scalability

## Status

- Branch: `refactor/code-refactor-2026-03-07`
- Current base: rebased on latest `master`
- Validation already run locally:
  - `dotnet build ExpekraCase.sln`
  - `dotnet test ExpekraCase.sln`

## Goal

Improve:

1. Orchestration clarity and testability
2. Ability to add more models with minimal changes
3. Ability to add/extend time-series features without schema breakage risk
4. Readability, naming consistency, and module structure

without changing current behavior/contracts unless explicitly planned.

## Updated Merge Strategy (Now That Master Is Synced)

Because this branch is already rebased onto current `master`, do not run a large late rebase again. Use small, mergeable slices.

### Recommended approach: stacked, phase-based PRs

1. Keep this branch as an integration umbrella.
2. For each phase, create a short-lived child branch from the latest accepted phase.
3. Open one PR per phase into `master`.
4. After each merge, fast-forward/rebase the umbrella branch to new `master` and continue.

This minimizes conflict risk and makes reviews behavior-focused.

### Why this is best now

- `master` already includes permutation-feature-importance changes.
- Most prior conflict risk is removed.
- Smaller PRs are easier to validate and safer to roll back.

## Guardrails

- Preserve existing CLI behavior and artifact file formats by default.
- Do not remove current modes (`all`, `part1`, `part2`, `part3`, `part4`, `diagnostics`).
- Keep public contracts stable unless a phase explicitly introduces contract changes.
- For each behavior-affecting change: add/update failing tests first, then implement.
- Run for each phase:
  - `dotnet build ExpekraCase.sln`
  - `dotnet test ExpekraCase.sln`
  - `dotnet test ExpekraCase.sln --collect:"XPlat Code Coverage"`

## Phase Plan

## Phase 0: Baseline Safety Net

### Scope

- Add characterization tests for current orchestration output contracts and model output shapes.
- Add tests that lock current CSV header/order expectations for touched modules.

### Exit criteria

- Existing behavior is pinned with tests before structural refactoring.

## Phase 1: Orchestration Extraction

### Scope

- Extract pipeline execution from `Program.cs` into `PipelineRunner` (imperative shell).
- Keep argument parsing in `Program.cs`.
- Keep output paths, mode names, and logging semantics unchanged.

### Detailed design (implementation-ready)

- Add `src/Forecasting.App/PipelineRunner.cs`.
- Introduce a small, explicit orchestration contract:
  - `PipelineMode` enum: `All`, `Part1`, `Part2`, `Part3`, `Part4`, `Diagnostics`.
  - `PipelineRunRequest` record: mode + resolved input/output paths + mode options (`ValidationWindowDays`, `EnablePfi`, `PfiHorizonStep`).
  - `PipelineRunResult` record: status + generated artifact paths + summary lines for console output.
- Keep path defaulting and CLI parsing in `Program.cs`; pass fully resolved values into the runner.
- Keep existing part modules unchanged in this phase (`Part1Preprocessing`, `Part2FeatureEngineering`, `Part3Modeling`, `Part4Evaluation`, `PartDiagnostics`).

### Ownership boundaries

- `Program.cs` owns:
  - CLI argument parsing and syntax validation.
  - Mapping parsed args to `PipelineRunRequest`.
  - Rendering final user-facing console lines from `PipelineRunResult`.
- `PipelineRunner` owns:
  - mode orchestration and part handoff.
  - required input file presence checks per mode.
  - ordering of artifact generation and write calls.
  - run-manifest emission for modes that currently produce manifests.
- Part modules own:
  - domain logic and transformations.
  - artifact serialization already encapsulated in each module.

### Inter-part handoff policy (CSV artifacts)

- Phase 1 keeps the current artifact-oriented handoff model intact (file-based CSV handoffs between parts).
- In `all` mode, orchestration must continue this explicit chain:
  - Part1 writes `part1_feature_matrix.csv`.
  - Part2 reads `part1_feature_matrix.csv`, writes `part2_supervised_matrix.csv`.
  - Part3 reads `part2_supervised_matrix.csv`, writes `part3_predictions.csv`.
  - Part4 reads `part2_supervised_matrix.csv` + `part3_predictions.csv`.
  - Diagnostics reads `part2_supervised_matrix.csv` + `part3_predictions.csv` (+ optional PFI CSV).
- `PipelineRunner` must treat these handoffs as explicit contracts by validating file existence at each required read boundary.
- Do not introduce in-memory handoff shortcuts in Phase 1.

### Planned evolution for handoffs (deferred)

- A future phase may add in-memory overloads for the `all` path to reduce repeated CSV parse/write round-trips.
- Any future optimization must preserve artifact emission and backward-compatible CLI behavior.

### Error-handling parity rules

- Preserve existing missing-file behavior: report the missing path and exit without unhandled exceptions.
- Preserve validation ordering (check required inputs before executing a part).
- Keep message shape stable in this phase unless a correction is required.

### Migration steps (small diffs)

1. Extract `all` mode to `PipelineRunner.RunAll(...)`.
2. Extract `diagnostics` mode to `PipelineRunner.RunDiagnostics(...)`.
3. Extract `part4` mode to `PipelineRunner.RunPart4(...)`.
4. Extract `part3` mode to `PipelineRunner.RunPart3(...)`.
5. Extract `part2` mode to `PipelineRunner.RunPart2(...)`.
6. Extract `part1` mode to `PipelineRunner.RunPart1(...)`.
7. Leave `Program.cs` as a thin dispatcher over parsed requests.

### Non-goals (explicitly out of scope)

- No model abstraction (`IForecastingModel`) in Phase 1.
- No feature-schema redesign in Phase 1.
- No artifact CSV/header/order changes in Phase 1.
- No replacement of CSV handoffs with in-memory pipelines in Phase 1.
- No DI container or broad infra rewiring in Phase 1.

### Acceptance test matrix for Phase 1

- `all` mode:
  - succeeds with valid inputs.
  - writes Part1/Part2/Part3/Part4/diagnostics artifacts.
  - preserves CSV handoff chain and expected cross-part input/output paths.
  - emits expected completion markers.
- `part1` mode:
  - fails gracefully when data/holidays path is missing.
  - succeeds and writes feature + audit artifacts.
- `part2` mode:
  - fails gracefully when Part1 input is missing.
  - succeeds and writes supervised matrix + summary.
- `part3` mode:
  - fails gracefully when Part2 input is missing.
  - succeeds and writes predictions + summary (+ optional PFI when enabled).
- `part4` mode:
  - fails gracefully when inputs are missing.
  - succeeds and writes metrics + sample.
- `diagnostics` mode:
  - fails gracefully when inputs are missing.
  - succeeds and writes diagnostics artifacts.

### Exit evidence to include in PR

- Before/after behavior notes per mode.
- Test additions/updates for mode orchestration behavior.
- Command output summary for:
  - `dotnet build ExpekraCase.sln`
  - `dotnet test ExpekraCase.sln`
  - `dotnet test ExpekraCase.sln --collect:"XPlat Code Coverage"`

### Exit criteria

- `Program.cs` becomes a thin dispatcher.
- All modes execute exactly as before.

## Phase 1.b: Hybrid Handoff Optimization (In-Memory + Artifacts)

### Why this phase

- Pure CSV handoffs are traceable but add repeated parse/write overhead and schema-coupling pressure.
- We want to keep auditability while improving orchestration efficiency and extensibility.

### Scope

- Add in-memory handoff path for `all` mode orchestration only.
- Keep artifact emission (`part1_feature_matrix.csv`, `part2_supervised_matrix.csv`, `part3_predictions.csv`, diagnostics/Part4 artifacts) for traceability.
- Keep standalone file-based modes (`part1`, `part2`, `part3`, `part4`, `diagnostics`) fully functional and backward compatible.
- Ensure both file-based and in-memory orchestration paths call the same domain transformation logic.

### Design constraints

- No duplicated business logic between in-memory and file-based paths.
- In-memory path may bypass intermediate re-read steps in `all` mode, but must still write artifacts.
- CSV schemas and artifact naming remain unchanged in this phase.

### Non-goals

- No artifact format/version change.
- No removal of file-based CLI modes.
- No model abstraction redesign in this phase (handled by Phase 2).

### Acceptance criteria

- `all` mode:
  - executes with in-memory inter-part handoff where available,
  - still writes the same artifact set,
  - produces equivalent key outputs/metrics to baseline within deterministic tolerance.
- `part1`..`diagnostics` modes:
  - behavior and outputs remain backward compatible.
- Characterization tests for artifact headers/contracts remain green.

### Phase 1.b implementation contract (clarifications)

- `all` mode handoff boundaries for in-memory optimization:
  - Part1 output rows may be handed directly to Part2 dataset-building logic.
  - Part2 supervised rows may be handed directly to Part3 modeling logic.
  - Part3 forecast rows may be handed directly to Part4/Diagnostics evaluation logic.
  - Even when handing off in memory, each stage must still emit the same artifact files currently produced in `all` mode.
- Standalone mode behavior remains file-oriented and unchanged:
  - `part1`, `part2`, `part3`, `part4`, and `diagnostics` continue to read/write through file contracts as they do today.
- Equivalence/tolerance policy for baseline vs hybrid comparisons:
  - Artifact row counts for Part1/Part2/Part3 must match exactly.
  - Part4 metric deltas (`MAE`, `RMSE`, `MAPE`) must be within absolute tolerance `1e-9` per model row.
  - Diagnostics summary shape and key grouping dimensions must match exactly.
- No logic duplication rule:
  - Hybrid and file-based orchestration paths must call the same core transformation/modeling methods.
  - New orchestration code in this phase should focus on data flow wiring only (not re-implementing domain logic).
- Run-manifest expectation:
  - Keep existing manifest fields and naming.
  - If a hybrid path flag is added later, it must be additive and backward compatible.
- Memory lifetime/release expectation for in-memory handoffs:
  - Keep large intermediate collections scoped to the smallest possible block.
  - Do not retain unnecessary references after downstream handoff/write is complete.
  - Prefer streaming/enumeration-friendly interfaces where practical, but do not change artifact contracts in this phase.

### Verification additions

- Compare baseline `all` output vs hybrid handoff output for:
  - row counts in Part1/Part2/Part3 artifacts,
  - model metrics in Part4,
  - diagnostics summary shape.
- Run:
  - `dotnet build ExpekraCase.sln`
  - `dotnet test ExpekraCase.sln`
  - `dotnet test ExpekraCase.sln --collect:"XPlat Code Coverage"`

## Phase 2: Model Plug-in Seam

### Scope

- Introduce a model contract (for example `IForecastingModel`) and a registry.
- Adapt existing models (`BaselineSeasonal`, `FastTreeRecursive`) to the contract.
- Replace hardcoded model loop with registry iteration.

### Exit criteria

- Adding a new model does not require editing central orchestration logic.
- Existing model outputs remain unchanged.

## Phase 3: Feature Contract Centralization

### Scope

- Centralize feature schema metadata and mapping logic used by Part2/Part3.
- Eliminate dual/duplicated feature-vector mapping paths.
- Keep current CSV column names/order unless explicitly versioned.

### Exit criteria

- One canonical feature mapping path.
- Reduced break risk when adding a feature.

## Phase 4: Shared CSV/Parsing Utilities

### Scope

- Consolidate duplicated CSV index/parse helper logic across Part3/Part4/Diagnostics.
- Keep strict validation behavior and explicit error messages.

### Exit criteria

- Parsing logic is unified and tested.
- No output contract regressions.

## Phase 5: Optional Scalability Layer

### Scope

- Introduce optional descriptor-based feature extension path for experiments.
- Keep strongly typed core features for readability and traceability.

### Exit criteria

- New experimental features can be added with limited cross-file edits.

## Review Checklist Per PR

- Scope is phase-limited (no future-phase bleed).
- Tests added/updated for changed behavior.
- No accidental artifact schema changes.
- Naming stays domain-specific and explicit (`AnchorUtcTime`, `HorizonStep`, `Split`).
- Build, tests, and coverage command results included in PR notes.

## Practical Next Step

Phase 0 is completed on this branch.

Start Phase 1 in a dedicated branch from `master`, for example:

- `refactor/phase1-orchestration-extraction`

Then proceed sequentially with Phase 2+ branches.
