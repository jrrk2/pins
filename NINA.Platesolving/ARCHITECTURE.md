# NINA.PlateSolving Architecture

## Purpose

`NINA.PlateSolving` provides a uniform orchestration layer over multiple plate-solving engines. It does not own image capture or telescope control itself; instead it coordinates those concerns through interfaces and mediators.

Build shape from `NINA.PlateSolving.csproj`:

- Target framework: `net10.0-windows`
- Output type: `Library`
- WPF enabled

## Top-Level Structure

- Root-level orchestration types
  `PlateSolverFactory`, `ImageSolver`, `CaptureSolver`, `CenteringSolver`, parameter/result/progress types
- `Interfaces/`
  `IPlateSolver`, `IImageSolver`, `ICaptureSolver`, `ICenteringSolver`, `IPlateSolverFactory`
- `Solvers/`
  Concrete solver integrations:
  - `ASTAPSolver`
  - `AstrometryPlateSolver`
  - `LocalPlateSolver` (astrometry.net's `solve-field`: through Cygwin's bash on Windows, run directly on Linux from the astrometry.net package and its index files)
  - `Platesolve2Solver`
  - `Platesolve3Solver`
  - `AllSkyPlateSolver`
  - `TheSkyXImageLinkSolver`
  - `Dc3PinPointSolver`
  - base classes such as `BaseSolver` and `CLISolver`

## Factory And Orchestration

`PlateSolverFactory.cs` is the central selection point:

- `GetPlateSolver(...)` maps `PlateSolverEnum` values to concrete solver implementations.
- `GetBlindSolver(...)` maps `BlindSolverEnum` values to the corresponding solver choice.
- pins: off Windows, a profile on ASTAP with no ASTAP installed solves with `LocalPlateSolver` when `solve-field` is installed (`ResolveSolver`), and new profiles default to `LOCAL` there (`PlateSolveSettings.SetDefaultValues`). The Origin driver's own alignment solves (`OriginAstrometryNet`) call `solve-field` directly and never go through the profile.
- `PlateSolverFactoryProxy` also constructs higher-level orchestration objects:
  - `ImageSolver`
  - `CaptureSolver`
  - `CenteringSolver`

Those orchestration types separate concerns:

- `ImageSolver`
  Solves an already prepared image.
- `CaptureSolver`
  Captures through `IImagingMediator`, optionally restores filter state, and retries according to `CaptureSolverParameter`.
- `CenteringSolver`
  Repeatedly solves and slews through imaging, telescope, filter wheel, and dome mediators until the target is centered.

## Solver Base Class

`Solvers/BaseSolver.cs` provides shared mechanics used by the concrete solvers:

- validates solver configuration through `EnsureSolverValid(...)`
- creates `PlateSolveImageProperties`
- manages a shared working directory and failed-solve directory under `%LOCALAPPDATA%\\NINA\\PlateSolver`

Concrete solvers only implement `SolveAsyncImpl(...)`.

## Dependency Position

Project references:

- `NINA.Core`
- `NINA.Astrometry`
- `NINA.Equipment`
- `NINA.Image`
- `NINA.Profile`

That matches the architecture:

- settings come from the profile layer
- image preparation comes from the imaging layer
- telescope/capture/filter/dome actions come from equipment mediators
- solver orchestration and engine-specific parsing live here

## Contribution Notes

- Add new solver integrations under `Solvers/` and register them in `PlateSolverFactory`.
- Keep solver-specific process execution, file parsing, and result translation inside the solver class.
- Keep capture/slew orchestration in `CaptureSolver` or `CenteringSolver`, not inside individual solver implementations.
- Reuse the existing `PlateSolveParameter`, `PlateSolveResult`, and `PlateSolveImageProperties` flow instead of inventing per-solver parameter objects unless the data is truly solver-private.
