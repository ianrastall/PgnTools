# PGN Tools — Architecture & Codebase Overview

> **Purpose of this file.** It exists to get a reviewer (human or LLM) oriented quickly so they
> can understand and audit the whole project from a repo dump. It describes the *current* state,
> the structure, the conventions, and — at the end — where an outside review is most valuable.

---

## 1. What this is

PGN Tools is a **Windows desktop app for working with PGN (chess) files at scale**: downloading
games from chess sites, organizing/cleaning large collections, enriching headers, and running
engine analysis. The central design rule is **stream games one at a time** so multi-gigabyte PGN
files never have to be loaded fully into memory.

It is a single GUI app, organized as grouped "bands" of tools, not a suite of separate programs.

## 2. Repository shape (important, and slightly unusual)

Three projects:

- **`PgnTools.Wpf/`** — the **actual shipping app**. WPF, .NET 10, Fluent dark theme. This is what
  builds and runs.
- **`PgnTools/`** — the original **WinUI 3** app. It is **not shipped**, but it is the source of
  truth for the **engine, services, and most view-models**. The WPF app reuses this code by
  **linking individual source files** (`<Compile Include="..\PgnTools\...\X.cs" Link="..." />` in
  `PgnTools.Wpf.csproj`) rather than by project reference. That lets it reuse the logic without
  pulling in WinUI / Windows App SDK dependencies.
- **`PgnTools.SmokeTests/`** — a console app that creates representative PGNs in temp files and
  exercises the services end-to-end (the CI runs it).

**Implication for reviewers:** most services and view-models physically live under
`PgnTools/Services` and `PgnTools/ViewModels/Tools`, but at runtime they are compiled *into the WPF
app* via those links. A file's folder is the legacy project; its runtime home is the WPF app.
WPF-only code (shell, infrastructure shims, theming, hub VMs) lives under `PgnTools.Wpf/`.

## 3. How the app is composed

- **Dependency injection** — `PgnTools.Wpf/App.xaml.cs` (`ConfigureServices`) registers every
  service, view-model, hub, and the main window in a `Microsoft.Extensions.DependencyInjection`
  container. **Everything is registered as a singleton.** `MainWindow` is resolved from the
  container at startup; the saved accent is applied before the window shows.
- **MVVM** — CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`).
  Tool view-models derive from `BaseViewModel` (which supplies `Title`, `StatusSeverity`,
  `StatusDetail`, and progress-detail helpers) and implement `IInitializable` / `IDisposable`.
- **Shell** — `MainWindowViewModel` exposes the hub VMs. `MainWindow.xaml` is one outer
  `TabControl` of bands; each band is a nested `TabControl` of tools. The hub VMs
  (`ChesscomHubViewModel`, `LichessToolsViewModel`, `AnalysisHubViewModel`,
  `EnrichmentHubViewModel`, `OrganizersHubViewModel`) just group child tool VMs and fan out
  `Initialize`/`Dispose`.

## 4. The tools (the bands wired into the WPF app)

- **Downloaders** — Chess.com (user archives, monthly crawl, events), Lichess (user games, monthly
  DB filter), Lc0, PGN Mentor, TWIC, Syzygy tablebases.
- **Organizers** — Filter, Sorter, Splitter, Merger, Deduplicator, Tour Breaker.
- **Enrichment** — ECO Tagger, Category Tagger, Elo Adder, Ply Count, Stockfish Normalizer,
  Unannotator.
- **Analysis** — PGN Info, Chess Analyzer (UCI/Stockfish), Elegance scoring, Checkmate Filter.
- **Engines** — Stockfish/Berserk source compiler (clone + build a UCI engine locally).
- **Settings** — accent color (follows the Windows system accent) and default folders.

Each tool has a deeper design doc under `Docs/` (e.g. `Docs/PgnFilterService.md`,
`Docs/ChessAnalyzerService.md`).

## 5. Core engine & shared patterns

- **Streaming I/O** — `PgnReader` is a hand-written char-level state-machine parser that yields
  `IAsyncEnumerable<PgnGame>` (handling tag pairs, brace/line comments, recursive variations, and
  NAGs). `PgnWriter` re-serializes a game (headers first, then word-wrapped move text). Tools read
  → filter/transform → write one game at a time.
- **Atomic output** — `FileReplacementHelper` writes to a temp file and then replaces the target,
  so a cancelled or failed run never corrupts the destination file.
- **Progress / status** — VMs expose `StatusMessage` / `StatusDetail` / `StatusSeverity` /
  `ProgressValue` / `IsRunning`. `BaseViewModel.BuildProgressDetail` formats "%, count, rate, ETA".
  The shared `ToolStatusView` control renders this at the bottom of every tool page.
- **Settings** — `IAppSettingsService` persists JSON to `%LOCALAPPDATA%/PgnTools/settings.json`;
  keys live in `AppSettingsKeys`. Each tool saves/restores its own inputs on dispose/init.
- **Cancellation** — long-running commands use a `CancellationTokenSource` plus a
  `SemaphoreSlim(1,1)` execution lock and a Cancel command.
- **Engine integration** — Chess Analyzer and Elegance drive a UCI engine (Stockfish) via async
  process I/O; the app can download a Stockfish build on demand.

## 6. WPF-specific infrastructure (why there is "WinUI-looking" code)

The legacy view-models were written against WinUI types. To run them unchanged under WPF,
`PgnTools.Wpf/Infrastructure/` provides shims:

- `InfoBarSeverity.cs` re-declares the `Microsoft.UI.Xaml.Controls.InfoBarSeverity` enum.
- `StorageShims.cs` re-declares `Windows.Storage.StorageFile` / `StorageFolder` (thin `Path`
  wrappers).
- `DataTransferShims.cs` re-declares `Windows.ApplicationModel.DataTransfer.DataPackage` /
  `Clipboard` (the compiler tool copies its build log) and routes them to the WPF clipboard.
- `ApplicationModelShims.cs` re-declares `Windows.ApplicationModel.Package`; `Package.Current`
  throws so the tablebase download-list probe falls back to `AppContext.BaseDirectory` (the WPF
  build is always unpackaged).
- `FilePickerHelper.cs` (WinForms/Win32 file dialogs), `WindowService.cs`, `AppSettingsService.cs`.

So a `using Microsoft.UI.Xaml.Controls;` inside a "WPF" file is expected and correct — it resolves
to the shim, not to WinUI. (`UseWindowsForms` is on for the file dialogs, but its implicit
`System.Windows.Forms` global using is removed in the csproj so `Clipboard` stays unambiguous.)

The one WinUI view that is genuinely re-implemented for WPF is the Chess.com **events** downloader:
`Controls/ChesscomEventsView.xaml(.cs)` hosts a WebView2 so the user can sign in to Chess.com, and
scrapes the auth cookie for the shared view-model (`Microsoft.Web.WebView2` is a WPF dependency).

## 7. Theming

- Fluent **dark** theme via `ThemeMode="Dark"` in `App.xaml` (an experimental WPF API; the
  `WPF0001` diagnostic is suppressed in the csproj).
- The accent color is read from the **live Windows system accent**
  (`AccentColorManager` reads `HKCU\Software\Microsoft\Windows\DWM\AccentColor`) and applied to the
  `AccentBrush` / `AccentSoftBrush` resources. Settings lets the user override it with a preset.
- Dark title bar via `DwmSetWindowAttribute` (immersive dark mode).
- **Caveat that is easy to regress:** an app-level implicit `Button` or `TabItem` style *without a
  full `ControlTemplate`* knocks that control back to the classic light theme. So: plain buttons
  use the Fluent default (no app style); primary buttons set `Background`/`Foreground` directly;
  tabs use full custom templates (`PrimaryTabStyle` pills for the top level, an implicit underline
  style for nested levels).

## 8. Build, run, release

- **Local build:** `build.cmd` (double-click) or
  `dotnet publish PgnTools.Wpf/PgnTools.Wpf.csproj -c Release -r win-x64 --self-contained true
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
  -p:IncludeNativeLibrariesForSelfExtract=true -o Build/PgnTools.Wpf.Release`. Output is a single
  self-contained `PgnTools.exe`. **Publish the WPF *project*, never the solution:** the legacy
  `PgnTools` WinUI project also emits `PgnTools.exe`, so a solution-level `publish -o <dir>` dumps
  both into one folder and they clobber each other nondeterministically. Both non-shipping projects
  (`PgnTools`, `PgnTools.SmokeTests`) set `IsPublishable=false` to guard against this, so an
  accidental solution publish still yields only the WPF app.
- **CI:** `.github/workflows/build.yml` builds + runs smoke tests + uploads an artifact on every
  push to `main`.
- **Release:** `.github/workflows/release.yml` is a manual (`workflow_dispatch`) button that builds
  and publishes a GitHub Release with the exe attached.
- **Target / deps:** `net10.0-windows`, nullable + implicit usings enabled. Key packages:
  CommunityToolkit.Mvvm, Gera.Chess, HtmlAgilityPack, Microsoft.Extensions.DependencyInjection /
  Hosting, Microsoft.Web.WebView2 (Chess.com events sign-in), ZstdSharp.Port (zstd for the bundled
  ratings DB and Lichess archives).

## 9. WinUI → WPF port status

The migration is **complete**: every tool from the legacy WinUI app is now wired into the WPF
shell and registered in DI. The most recently ported tools (Chess.com Events, Syzygy Tablebases,
and the Stockfish/Berserk Compiler) reuse their legacy view-models and services via linked
compiles like everything else; only the Chess.com events *view* was re-implemented for WPF (see
§6). The `PgnTools/` WinUI project is retained solely as the source of truth for that shared code.

## 10. Where an outside review is most valuable

Suggested focus areas, roughly highest-value first:

1. **Streaming parser correctness** (`PgnReader`) — comment/variation/NAG edge cases, malformed
   PGN, encoding/BOM handling, extremely long lines, games split across buffer boundaries.
2. **Filtering logic** — Chess.com filters use structured JSON; Lichess filters
   (`LichessDownloaderService`) are derived from PGN headers/move text. Scrutinize the Lichess
   heuristics: win detection (`White`/`Black`/`Result`), checkmate (`#` in move text), bullet
   (`Event` / `TimeControl`), and non-standard (`Variant`).
3. **Concurrency & cancellation** — the download/analysis loops, the `SemaphoreSlim` execution
   locks, disposal ordering, and whether every cancellation/error path cleans up temp files.
4. **Process / engine handling** — Chess Analyzer, Elegance, Stockfish downloader: process
   lifetime, stdout/stderr draining (deadlock risk), and untrusted-binary considerations.
5. **File-IO safety** — `FileReplacementHelper` atomicity, path handling, overwrite behavior,
   same-input-as-output guards.
6. **DI lifetimes** — everything is a singleton; check for shared mutable state or thread-safety
   issues across tools that could collide.
7. **Network** — `HttpClient` usage and lifetime, Chess.com rate limiting, large streamed
   downloads, and error handling/retries.
8. **Memory on huge files** — confirm no path accidentally buffers an entire file (the whole point
   of the streaming design).

## 11. Further pointers

- Per-tool design docs: `Docs/*.md`.
- Migration history and decisions (including the recent dark-theme/tab/release work):
  `Docs/Wpf-Rewrite-Handoff.md`.
- The project's own coding guidance (written for the legacy WinUI era, but the patterns still
  apply): `PgnTools/Agents.md`.
