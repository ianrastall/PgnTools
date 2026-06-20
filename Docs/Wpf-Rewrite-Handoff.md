# PgnTools WPF Rewrite Handoff

Last updated: June 20, 2026

## Purpose

This document explains the current local rewrite state of PgnTools after the repository direction changed from WinUI 3 to WPF. It is meant to let a future collaborator continue the work without reverse-engineering the repo from scratch.

## Current Repo Strategy

- `PgnTools.Wpf/` is the primary local application.
- `PgnTools/` is the legacy WinUI 3 application kept for feature reference and source reuse.
- The intended end state is one WPF GUI, not a suite of separate desktop apps.
- The current migration principle is: keep scope small, keep shared PGN logic reusable, and port workflows in coherent groups.

## Git State

As of June 20, 2026:

- Current local branch: `grouped-nav-v2`
- Local `HEAD` and `origin/main` both point to commit `042b2fc`
- The rewrite work is still local and unpushed
- No GitHub users have seen these changes yet

This means the rewrite can continue freely without disrupting the public repo until it is intentionally pushed later.

## What Was Built

### 1. New WPF application

Project:

- [PgnTools.Wpf.csproj](</D:/GitHub/PgnTools/PgnTools.Wpf/PgnTools.Wpf.csproj>)

Primary shell:

- [App.xaml.cs](</D:/GitHub/PgnTools/PgnTools.Wpf/App.xaml.cs>)
- [MainWindow.xaml](</D:/GitHub/PgnTools/PgnTools.Wpf/MainWindow.xaml>)
- [MainWindow.xaml.cs](</D:/GitHub/PgnTools/PgnTools.Wpf/MainWindow.xaml.cs>)

The WPF app uses dependency injection and links selected existing source files from the legacy WinUI app instead of duplicating service logic.

### 2. WPF-native infrastructure seams

These replace the WinUI-specific bits that were embedded in the original view-model layer:

- [AppSettingsService.cs](</D:/GitHub/PgnTools/PgnTools.Wpf/Infrastructure/AppSettingsService.cs>)
- [WindowService.cs](</D:/GitHub/PgnTools/PgnTools.Wpf/Infrastructure/WindowService.cs>)
- [FilePickerHelper.cs](</D:/GitHub/PgnTools/PgnTools.Wpf/Infrastructure/FilePickerHelper.cs>)
- [InfoBarSeverity.cs](</D:/GitHub/PgnTools/PgnTools.Wpf/Infrastructure/InfoBarSeverity.cs>)
- [StorageShims.cs](</D:/GitHub/PgnTools/PgnTools.Wpf/Infrastructure/StorageShims.cs>)

The goal was to let existing downloader-focused view-models run in WPF with minimal invasive rewriting.

### 3. Migrated workflow bands

The WPF shell currently wires up these downloader workflows:

- Chess.com user archives
- Chess.com monthly crawl
- Lichess user downloader
- Lichess monthly DB filter
- Lc0 monthly archive builder
- PGN Mentor downloader
- TWIC downloader

The WPF shell also wires up this organizer band:

- Filter
- Sorter
- Splitter
- Merger
- Deduplicator
- Tour Breaker

The WPF shell now wires up this enrichment band:

- ECO Tagger
- Elo Adder
- Category Tagger
- Ply Count Adder
- Stockfish Normalizer
- Chess Unannotator

The WPF shell now wires up this analysis band:

- PGN Info
- Chess Analyzer
- Elegance
- Checkmate Filter

These were grouped first because they represent the highest-value current workflows and prove the overall architecture.

The WPF shell now also has a **Settings** tab:

- Accent color (16 presets plus the built-in app default), applied live and persisted
- Default tablebases folder
- Default download folder

These folder defaults are read by the downloader and analysis view-models as their starting
locations, so the Settings tab is functional, not cosmetic. Accent switching is handled by a
WPF-native [AccentColorManager.cs](</D:/GitHub/PgnTools/PgnTools.Wpf/Infrastructure/AccentColorManager.cs>)
that swaps the `AccentBrush` / `AccentSoftBrush` application resources (the WinUI
`AccentColorManager` was not portable, so it was not linked). The saved accent is applied at
startup in `App.OnStartup`.

## Dark Theme & System Accent (June 20, 2026)

The WPF shell now runs in dark mode and follows the user's Windows accent color:

- `App.xaml` sets `ThemeMode="Dark"` (WPF Fluent dark theme, so every control — combo boxes,
  tabs, scroll bars, popups — renders dark, not just the surfaces colored by hand). The
  experimental `WPF0001` diagnostic is suppressed via `<NoWarn>` in the csproj.
- The custom surface palette (`SurfaceBrush`, `SurfaceStrongBrush`, `BorderBrush`,
  `TextPrimaryBrush`, `TextSecondaryBrush`) was recolored to dark values.
- [AccentColorManager.cs](</D:/GitHub/PgnTools/PgnTools.Wpf/Infrastructure/AccentColorManager.cs>)
  reads the live Windows accent from `HKCU\Software\Microsoft\Windows\DWM\AccentColor`
  (`TryGetSystemAccent`). The "Windows accent (system)" option (the default) resolves to it; the
  16 named presets remain as optional overrides. `AccentSoftBrush` is tinted toward the dark
  surface instead of white.
- [MainWindow.xaml.cs](</D:/GitHub/PgnTools/PgnTools.Wpf/MainWindow.xaml.cs>) forces an immersive
  dark title bar via `DwmSetWindowAttribute` (DWMWA_USE_IMMERSIVE_DARK_MODE).
- `ToolStatusView` severity backgrounds were darkened to readable deep tints.
- The oversized header card (title + blurb + "Primary Local App" badge) was replaced with a
  single compact "PGN Tools" title line to save vertical space.

### UI cleanup (June 20, 2026)

- The assembly is renamed to **`PGN Tools`** via `<AssemblyName>` in the csproj, so the built
  executable is `PGN Tools.exe` (not `PgnTools.Wpf.exe`).
- Marketing prose was removed: the four "X Workbench" headings + their descriptions, and the
  gray one-sentence description at the top of each tool page (21 of them).
- The per-page status/progress panel (`controls:ToolStatusView`) is present on all 24 tool pages
  (one per page, bound to that page's VM). It shows the status message + detail text and an
  indeterminate progress bar while `IsRunning` is true. (It was briefly removed and then restored
  with a uniform binding; the detail line already carries numeric progress/ETA text via
  `BaseViewModel.BuildProgressDetail`, so the indeterminate bar plus that text reads well.)
- The three tab levels are now visually distinct: the top-level TabControl uses
  `PrimaryTabStyle` (filled accent pills via `ItemContainerStyle`); all nested TabItems use the
  implicit underline-tab style. Both styles live in `App.xaml`. (Note: like buttons, a TabItem
  style must provide a full `ControlTemplate` or it reverts to the classic light theme.)

### Lichess user-games filters (June 20, 2026)

The Lichess **User Games** downloader now has the same four filters as Chess.com User Archives
(only user wins, only checkmates, exclude bullet, exclude non-standard variants). Because the
Lichess API returns a raw PGN stream (not structured JSON like Chess.com), the filters are applied
as a streaming post-download pass: `LichessDownloaderService` downloads to a temp file, then (only
when `LichessUserGameFilters.IsActive`) re-reads it with `PgnReader`, keeps games matching the
filters, and writes them with `PgnWriter` before replacing the output. Filtering is derived from
PGN headers/movetext: `White`/`Black`/`Result` for wins, `#` in the move text for checkmate,
`Event`/`TimeControl` for bullet, and the `Variant` header for non-standard. The service now takes
`PgnReader`/`PgnWriter` via its constructor (both already DI-registered). The other downloaders
(Lc0, PGN Mentor, TWIC) are bulk/aggregate fetches and intentionally have no per-game filters.

### Button theming caveat

Do **not** add an app-level `<Style TargetType="Button">` (implicit or `BasedOn="{StaticResource
{x:Type Button}}"`). Either one knocks WPF's Fluent dark button template back to the classic
light template, so every button renders light. Plain buttons must have no app Style so they use
the Fluent default; primary actions set `Background="{DynamicResource AccentBrush}"` /
`Foreground="White"` directly on the button (the Fluent template honors `Background` in the
enabled state, so they show the accent color when enabled and dim when disabled).

## What Still Lives Only In WinUI

The following areas are not yet properly ported into WPF:

- Compiler and engine-management surfaces
- Chess.com events downloader and tablebase downloader surfaces
- App-polish work beyond the current WPF shell

The legacy WinUI files remain the source of truth for behavior during those ports.

## Startup Bug Fixed (June 20, 2026)

`IPgnInfoService` was never registered in `App.ConfigureServices`, so resolving the full
view-model graph threw at startup (`Unable to resolve service for type
'PgnTools.Services.IPgnInfoService'`) and the WPF app could not launch. The registration was
added next to the other PGN services. The remaining unregistered service interfaces
(`IBerserkCompilerService`, `IChesscomEventsDownloaderService`, `IStockfishCompilerService`,
`ITablebaseDownloaderService`) are only used by view-models that are not yet wired into the WPF
shell, so they are intentionally absent for now.

## Important Local Reality

There is also an older, unfinished local WinUI grouped-navigation experiment still present as untracked files under `PgnTools/Views/Tools` and `PgnTools/ViewModels/Tools`.

Those files were not pushed and were intentionally left alone. They can be used as design/reference material if helpful, but they are not the active rewrite direction.

## Build Commands

Primary WPF app:

```powershell
dotnet build PgnTools.Wpf\PgnTools.Wpf.csproj
dotnet run --project PgnTools.Wpf\PgnTools.Wpf.csproj
```

Smoke tests:

```powershell
dotnet run --project PgnTools.SmokeTests\PgnTools.SmokeTests.csproj
```

Release publish:

```powershell
dotnet publish PgnTools.Wpf\PgnTools.Wpf.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o Build\PgnTools.Wpf.Release
```

Legacy WinUI app:

```powershell
dotnet build PgnTools\PgnTools.csproj
```

## Current Smoke-Test Coverage

The local smoke-test project creates temporary representative PGNs and exercises:

- PGN Info
- Checkmate Filter
- Filter
- Sorter
- Splitter
- Merger
- Deduplicator
- Tour Breaker
- ECO Tagger
- Elo Adder
- Category Tagger
- Ply Count Adder
- Stockfish Normalizer
- Chess Unannotator
- Chess Analyzer
- Elegance

As of June 20, 2026, a local run passed 16/16 tests, including depth-1 Stockfish-backed checks for Chess Analyzer and Elegance using cached local Stockfish.

## Suggested Next Steps

1. Do a manual UI pass over the WPF shell with the published executable.
2. Revisit compiler, engine-management, and settings surfaces once the main user-facing bands are stable.
3. Add any missing WPF polish around long-running engine workflows after real-file testing.
4. Decide later whether to rename `PgnTools.Wpf/` to `PgnTools/` after the migration is far enough along to justify a bigger local move.
5. Only push to GitHub once the WPF app feels like a coherent replacement rather than a promising partial.

## Why This Structure Was Chosen

The repo owner explicitly preferred one application with smaller, coherent scope over a suite of separate GUIs. The WPF rewrite follows that rule:

- one app
- grouped tool sections
- shared core logic
- fewer duplicated UI seams
- less packaging and update complexity later
