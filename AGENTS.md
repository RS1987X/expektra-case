# AGENTS.md (Project Instructions for AI Coding Agents)

This file is **agent-facing** and should be read before making changes.

It exists to reduce repeated mistakes by capturing project-specific rules and verification steps.

## Repository tech stack

- Primary language/runtime: **C# with .NET 10**.
- Source of truth tooling: use `.NET CLI` (`dotnet restore/build/test`) for verification.

## How this file fits the workflow

- **Task-specific scope** lives in the plan/issue/PR description (what to build).
- **Repo-specific rules** live here (how to build it in this repo).
- If anything conflicts: the plan defines scope; `AGENTS.md` defines constraints + verification.

## Do first (every task)

1) Read the relevant plan/spec:
  - `docs/CASE_EXECUTION_PLAN.md` (or the issue/PR description)
2) Confirm scope:
   - What phase/slice are we implementing?
   - What is explicitly out of scope?
3) Identify verification commands for this repo and run them before shipping.

## Hard constraints (never violate)

- Follow the plan scope. Do **not** implement later phases.
- Keep diffs minimal; **no refactoring unless the plan explicitly requires it**.
  - Don't move files, rename modules, or reorganize folder structure unless the plan says to.
  - Don't remove existing code/files unless the plan explicitly deprecates them.
  - If you must refactor to implement the feature, document why in the PR and get explicit approval.
- Do not change public APIs/exports unless the plan explicitly calls for it.
- Do not add new dependencies without explicit approval in the plan/issue.
- For behavior changes, follow red-green-refactor: first add/update a failing automated test that captures the requirement, then implement code until tests pass.
- If a behavior change cannot reasonably be tested, explicitly document why in the PR/delivery notes.
- No secrets/credentials in code, logs, tests, or commits.

## Repo conventions

- Prefer updating/adding tests alongside code changes.
- Prefer small, reviewable commits (even if a bot/agent is producing them).
- If you change behavior, update docs where users/developers will look.
- Comment non-obvious design decisions in code close to where they are applied.
- Use `gh` CLI for creating issues and pull requests (not just markdown files).

## Branching workflow (required)

When starting an issue/phase implementation:

1. Sync integration branch first:
  - `git checkout master`
  - `git pull --ff-only`
2. Create a dedicated feature branch from updated `master`:
  - `git checkout -b <feature-branch>`
3. Implement only in-scope changes on that feature branch.
4. Before opening PR, ensure latest `master` changes are incorporated (rebase or merge as appropriate), then rerun verification.
5. Open PR from feature branch into `master` and include required delivery details listed below.

## Documentation deliverables

When implementing a feature, also deliver as applicable:

- **ADRs (Architecture Decision Records)**: If you make a meaningful design choice (storage tech, algorithm, architecture pattern), document it in `docs/decisions/NNN-title.md`.
  - Examples: choosing SQLite vs in-memory, TF-IDF vs BM25, fusion strategies
  - Use sequential numbering (check existing files first)
- **ARCHITECTURE.md updates**: If you change module structure, state flows, or component boundaries, update `docs/ARCHITECTURE.md` (or create it if missing).
- **FOLLOW_UPS.md**: If review identifies non-blocking improvements you're deferring, add them to `docs/FOLLOW_UPS.md` (or create it if missing) with links back to the review/PR.

## Functional core / imperative shell (recommended)

This is a pragmatic style guideline to make changes easier to review and test.

- Keep I/O at the edges: filesystem/network/DB calls live in thin adapters.
- Keep the core logic pure where practical: functions take plain inputs and return plain outputs.
- Avoid mutating inputs; prefer returning new values.
- Pass dependencies explicitly (clients, config, clock, RNG) instead of globals/singletons.
- Make data shapes explicit (records/classes/DTOs) to avoid “invented fields”.
- Handle errors explicitly (clear exceptions or Result-like return objects); don’t hide failures in logs.
- Prefer simple composition over heavy abstractions; match existing codebase patterns.

## Verification (must run before PR/merge)

Preferred: run `./scripts/verify.sh` from the repo root if present.

If `scripts/verify.sh` is not present, run the .NET checks below.

If your repo needs additional checks beyond what `verify.sh` runs by default, list them below and update `scripts/verify.sh` accordingly.

### .NET (primary for this repo)

- `dotnet restore ExpekraCase.sln`
- `dotnet build ExpekraCase.sln`
- `dotnet test ExpekraCase.sln`
- For implementation phases that change behavior, run a coverage pass after tests (for example `dotnet test ExpekraCase.sln --collect:"XPlat Code Coverage"`) and review coverage for touched modules.
- For behavior-changing work, target **branch coverage >= 75% for touched modules** (or no regression from the module's existing baseline when legacy code is below target).
  - If target cannot be met in the same change, document why in PR/delivery notes and create a follow-up item with concrete test additions.

Do not mark work complete until the relevant tests and the full test command above pass.

### App smoke check (optional)

- `dotnet run --project src/Forecasting.App/Forecasting.App.csproj --no-build`

## PR / delivery requirements

In the PR description (or as a comment), include:

- Branch name (especially if it is `codex/...`)
- Scope implemented (what you did / didn’t do)
- Tests added/updated (or explicit reason no tests were added)
- Commands run + results (paste output or summarize)
- Any known limitations / follow-ups
- ADRs created (if key decisions were made)
- Docs updated (ARCHITECTURE.md, README, etc.)

## Self-improvement loop (update this file)

After a bug, review comment, or failed CI run:

1) Add a short rule in **Mistakes we don’t repeat**.
2) Add/adjust a verification command in **Verification**.
3) If the plan template was missing something, update `docs/CASE_EXECUTION_PLAN.md`.
  - If the file does not exist yet, create it only when the team adopts a plan-template workflow.

### Mistakes we don’t repeat

- If changing CLI flags or command usage, update `README.md` and any existing run/instructions documentation under `docs/`.
- If changing shared contracts/DTOs, add or update unit tests that cover serialization and API behavior.
- After each implementation phase, do not stop at green tests only; run coverage and verify touched modules have test coverage evidence.
- If CI/build fails, reproduce locally in a clean tree (`git clean -fdx`) and rerun `.NET` checks.
- Never remove/move/reorganize existing code unless the plan explicitly calls for it.
- For meaningful design choices, add an ADR under `docs/decisions/`.

## Pointers

- Case execution plan: `docs/CASE_EXECUTION_PLAN.md`
- Execution/leakage guide: `docs/EXECUTION_ORDER_AND_LEAKAGE_GUIDE.md`
- Solution file: `ExpekraCase.sln`
- Environment setup: `global.json`
