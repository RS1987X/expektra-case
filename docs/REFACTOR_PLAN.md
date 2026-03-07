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

### Exit criteria

- `Program.cs` becomes a thin dispatcher.
- All modes execute exactly as before.

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

Start with Phase 0 in a dedicated branch from `master`, for example:

- `refactor/phase0-safety-net`

Then proceed sequentially with phase branches.
