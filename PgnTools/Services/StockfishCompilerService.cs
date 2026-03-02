using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PgnTools.Services;

public enum StockfishBuildTarget
{
    ProfileBuild,
    Build
}

public enum StockfishCompileStage
{
    Preparing,
    Toolchain,
    Cloning,
    DownloadingNetwork,
    Building,
    Stripping,
    Finalizing,
    Completed
}

public sealed record StockfishCompileOptions(
    string WorkspaceFolder,
    string SourceRef,
    string Architecture,
    string Compiler,
    StockfishBuildTarget BuildTarget,
    int Jobs,
    bool DownloadNetwork,
    bool StripExecutable,
    bool AutoInstallToolchain,
    string? GitHubPat);

public sealed record StockfishCompileProgress(
    StockfishCompileStage Stage,
    string Message,
    double? Percent = null);

public sealed record StockfishCompileResult(
    string SourceReference,
    string RepositoryPath,
    string OutputBinaryPath,
    string Architecture,
    string Compiler,
    StockfishBuildTarget BuildTarget);

public sealed record StockfishMsysRootStatus(
    string RootPath,
    bool HasMake,
    bool HasMingwCompiler,
    bool HasClangCompiler,
    bool HasMingwBinutils,
    bool HasNetworkDownloader);

public sealed record StockfishToolchainProbe(
    string RequestedCompiler,
    string SelectedCompiler,
    string RecommendedCompiler,
    string? ActiveMsysRoot,
    bool IsMsys2Installed,
    bool IsReadyForSelection,
    IReadOnlyList<StockfishMsysRootStatus> Roots,
    string Summary);

public interface IStockfishCompilerService
{
    Task<StockfishToolchainProbe> ProbeToolchainAsync(
        string compiler,
        bool downloadNetwork,
        IProgress<string>? output = null,
        CancellationToken ct = default);

    Task<StockfishToolchainProbe> InstallOrRepairToolchainAsync(
        string compiler,
        bool downloadNetwork,
        IProgress<string>? output = null,
        CancellationToken ct = default);

    Task<StockfishCompileResult> CompileAsync(
        StockfishCompileOptions options,
        IProgress<StockfishCompileProgress>? progress = null,
        IProgress<string>? output = null,
        CancellationToken ct = default);
}

public sealed partial class StockfishCompilerService : IStockfishCompilerService
{
    private static readonly Uri StockfishRepository = new("https://github.com/official-stockfish/Stockfish.git");

    private static readonly string[] PreferredMsysRoots =
    [
        @"C:\msys64",
        @"D:\msys64"
    ];

    private static readonly TimeSpan MsysInstallProbeDelay = TimeSpan.FromSeconds(2);
    private const int MsysInstallProbeAttempts = 10;

    public async Task<StockfishToolchainProbe> ProbeToolchainAsync(
        string compiler,
        bool downloadNetwork,
        IProgress<string>? output = null,
        CancellationToken ct = default)
    {
        var requestedCompiler = NormalizeCompilerSelection(compiler);
        return await ProbeToolchainInternalAsync(requestedCompiler, downloadNetwork, output, ct).ConfigureAwait(false);
    }

    public async Task<StockfishToolchainProbe> InstallOrRepairToolchainAsync(
        string compiler,
        bool downloadNetwork,
        IProgress<string>? output = null,
        CancellationToken ct = default)
    {
        var requestedCompiler = NormalizeCompilerSelection(compiler);
        var probe = await ProbeToolchainInternalAsync(requestedCompiler, downloadNetwork, output, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(probe.ActiveMsysRoot))
        {
            output?.Report("MSYS2 not detected. Attempting installation via winget...");
            await InstallMsys2Async(output, ct).ConfigureAwait(false);
            probe = await WaitForMsys2Async(requestedCompiler, downloadNetwork, output, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(probe.ActiveMsysRoot))
        {
            throw BuildToolchainException(
                probe.SelectedCompiler,
                downloadNetwork,
                "MSYS2 installation could not be verified.");
        }

        var shell = CreateShellEnvironment(probe.ActiveMsysRoot, probe.SelectedCompiler);
        output?.Report($"Installing or repairing MSYS2 packages in '{probe.ActiveMsysRoot}'...");
        await InstallRequiredPackagesAsync(shell, probe.SelectedCompiler, downloadNetwork, output, ct).ConfigureAwait(false);

        var refreshed = await ProbeToolchainInternalAsync(requestedCompiler, downloadNetwork, output, ct).ConfigureAwait(false);
        if (!refreshed.IsReadyForSelection)
        {
            throw BuildToolchainException(
                refreshed.SelectedCompiler,
                downloadNetwork,
                "Toolchain is still incomplete after installation.");
        }

        return refreshed;
    }
    public async Task<StockfishCompileResult> CompileAsync(
        StockfishCompileOptions options,
        IProgress<StockfishCompileProgress>? progress = null,
        IProgress<string>? output = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sourceRef = NormalizeSourceRef(options.SourceRef);
        var architecture = NormalizeBuildToken(options.Architecture, nameof(options.Architecture));
        var requestedCompiler = NormalizeCompilerSelection(options.Compiler);
        var jobs = Math.Clamp(options.Jobs, 1, Math.Max(1, Environment.ProcessorCount * 2));
        var workspaceFolder = NormalizeWorkspace(options.WorkspaceFolder);

        progress?.Report(new StockfishCompileProgress(
            StockfishCompileStage.Preparing,
            "Preparing workspace...",
            5));

        Directory.CreateDirectory(workspaceFolder);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var buildRoot = Path.Combine(workspaceFolder, $"stockfish-build-{stamp}");
        var repositoryPath = Path.Combine(buildRoot, "Stockfish");
        Directory.CreateDirectory(buildRoot);

        output?.Report($"Workspace: {workspaceFolder}");
        output?.Report($"Build directory: {buildRoot}");

        progress?.Report(new StockfishCompileProgress(
            StockfishCompileStage.Toolchain,
            "Detecting compiler toolchain...",
            12));

        var preparedToolchain = await PrepareToolchainAsync(
            requestedCompiler,
            options.DownloadNetwork,
            options.AutoInstallToolchain,
            output,
            ct).ConfigureAwait(false);
        var compatibilityMakeArgs = BuildCompatibilityMakeArgs(sourceRef, preparedToolchain.Compiler);
        var usesNnueEmbeddingWorkaround = !string.IsNullOrWhiteSpace(compatibilityMakeArgs);

        output?.Report(
            $"Build settings: target={options.BuildTarget}, arch={architecture}, compiler={preparedToolchain.Compiler}, jobs={jobs}, net={options.DownloadNetwork}, strip={options.StripExecutable}");
        output?.Report($"Compiler selected: {preparedToolchain.Compiler} ({DescribeCompiler(preparedToolchain.Compiler)})");
        output?.Report($"MSYS2 root: {preparedToolchain.Shell.MsysRoot}");
        if (usesNnueEmbeddingWorkaround)
        {
            output?.Report(
                "Applying compatibility workaround for sf_17 on mingw: disabling embedded NNUE symbols during compile.");
        }

        progress?.Report(new StockfishCompileProgress(
            StockfishCompileStage.Cloning,
            $"Cloning Stockfish ({sourceRef})...",
            20));

        await CloneRepositoryAsync(buildRoot, repositoryPath, sourceRef, options.GitHubPat, output, ct).ConfigureAwait(false);

        var sourceDirectory = Path.Combine(repositoryPath, "src");
        if (!Directory.Exists(sourceDirectory))
        {
            throw new InvalidOperationException($"Stockfish source directory not found: {sourceDirectory}");
        }

        if (options.DownloadNetwork)
        {
            progress?.Report(new StockfishCompileProgress(
                StockfishCompileStage.DownloadingNetwork,
                "Downloading default NNUE network...",
                35));

            try
            {
                await RunBashCommandAsync(
                    preparedToolchain.Shell,
                    sourceDirectory,
                    "make net",
                    output,
                    ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                var localNets = Directory.EnumerateFiles(sourceDirectory, "nn-*.nnue", SearchOption.TopDirectoryOnly)
                    .ToList();
                if (localNets.Count > 0)
                {
                    output?.Report(
                        $"make net failed, but {localNets.Count} local NNUE file(s) exist. Continuing build. Error: {ex.Message}");
                }
                else
                {
                    throw new InvalidOperationException(
                        "Failed to download NNUE networks. You can retry, check network/proxy settings, or disable 'Run make net' if local nn-*.nnue files are present.",
                        ex);
                }
            }
        }

        var requestedBuildTarget = options.BuildTarget;
        var buildTarget = requestedBuildTarget == StockfishBuildTarget.ProfileBuild
            ? "profile-build"
            : "build";
        var effectiveBuildTarget = requestedBuildTarget;

        progress?.Report(new StockfishCompileProgress(
            StockfishCompileStage.Building,
            $"Compiling Stockfish ({buildTarget})...",
            65));

        var compilerOverride = GetCompilerCxxOverride(preparedToolchain.Compiler);
        try
        {
            await RunBashCommandAsync(
                preparedToolchain.Shell,
                sourceDirectory,
                $"set -euo pipefail; make -j {jobs} {buildTarget} ARCH={architecture} COMP={preparedToolchain.Compiler} COMPCXX={compilerOverride}{compatibilityMakeArgs}",
                output,
                ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (requestedBuildTarget == StockfishBuildTarget.ProfileBuild)
        {
            output?.Report($"Profile build failed: {ex.Message}");
            output?.Report("Retrying with standard build target (build, no PGO)...");
            progress?.Report(new StockfishCompileProgress(
                StockfishCompileStage.Building,
                "Retrying with standard build (no PGO)...",
                72));

            await RunBashCommandAsync(
                preparedToolchain.Shell,
                sourceDirectory,
                $"set -euo pipefail; make clean ARCH={architecture} COMP={preparedToolchain.Compiler} COMPCXX={compilerOverride}",
                output,
                ct).ConfigureAwait(false);

            buildTarget = "build";
            effectiveBuildTarget = StockfishBuildTarget.Build;
            await RunBashCommandAsync(
                preparedToolchain.Shell,
                sourceDirectory,
                $"set -euo pipefail; make -j {jobs} {buildTarget} ARCH={architecture} COMP={preparedToolchain.Compiler} COMPCXX={compilerOverride}{compatibilityMakeArgs}",
                output,
                ct).ConfigureAwait(false);
        }

        if (options.StripExecutable)
        {
            progress?.Report(new StockfishCompileProgress(
                StockfishCompileStage.Stripping,
                "Stripping executable...",
                85));

            await RunBashCommandAsync(
                preparedToolchain.Shell,
                sourceDirectory,
                $"set -euo pipefail; make strip ARCH={architecture} COMP={preparedToolchain.Compiler} COMPCXX={compilerOverride}",
                output,
                ct).ConfigureAwait(false);
        }

        progress?.Report(new StockfishCompileProgress(
            StockfishCompileStage.Finalizing,
            "Finalizing compiled binary...",
            95));

        var builtBinaryPath = LocateBuiltBinary(sourceDirectory);
        var versionSegment = ResolveStockfishVersionSegment(sourceRef);
        var versionDirectory = EnginePackageLayout.BuildVersionDirectory(
            workspaceFolder,
            "stockfish",
            versionSegment);
        EnginePackageLayout.PrepareVersionDirectory(versionDirectory);

        var outputBinaryPath = EnginePackageLayout.CopyPrimaryBinary(
            builtBinaryPath,
            versionDirectory,
            output);
        EnginePackageLayout.CopyRuntimeAssets(
            sourceDirectory,
            versionDirectory,
            builtBinaryPath,
            output);

        if (usesNnueEmbeddingWorkaround &&
            !Directory.EnumerateFiles(sourceDirectory, "nn-*.nnue", SearchOption.TopDirectoryOnly).Any())
        {
            output?.Report(
                "NNUE embedding was disabled for this build, but no nn-*.nnue files were found in the build folder. " +
                "Place the required network file next to the compiled executable.");
        }

        await TryRunBashCommandAsync(
            preparedToolchain.Shell,
            sourceDirectory,
            $"set -euo pipefail; make clean ARCH={architecture} COMP={preparedToolchain.Compiler} COMPCXX={compilerOverride}",
            output,
            ct).ConfigureAwait(false);
        await TryRunBashCommandAsync(
            preparedToolchain.Shell,
            sourceDirectory,
            $"set -euo pipefail; make profileclean ARCH={architecture} COMP={preparedToolchain.Compiler} COMPCXX={compilerOverride}",
            output,
            ct).ConfigureAwait(false);

        EnginePackageLayout.MoveRepositoryToSourceFolder(repositoryPath, versionDirectory, output);
        SafeDeleteDirectory(buildRoot);

        output?.Report($"Output binary: {outputBinaryPath}");

        progress?.Report(new StockfishCompileProgress(
            StockfishCompileStage.Completed,
            "Compilation completed.",
            100));

        return new StockfishCompileResult(
            sourceRef,
            Path.Combine(versionDirectory, "src"),
            outputBinaryPath,
            architecture,
            preparedToolchain.Compiler,
            effectiveBuildTarget);
    }

    private async Task<PreparedToolchain> PrepareToolchainAsync(
        string requestedCompiler,
        bool downloadNetwork,
        bool autoInstall,
        IProgress<string>? output,
        CancellationToken ct)
    {
        var probe = await ProbeToolchainInternalAsync(requestedCompiler, downloadNetwork, output, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(probe.ActiveMsysRoot))
        {
            if (!autoInstall)
            {
                throw BuildToolchainException(
                    probe.SelectedCompiler,
                    downloadNetwork,
                    "MSYS2 was not detected.");
            }

            output?.Report("MSYS2 not detected. Attempting installation via winget...");
            await InstallMsys2Async(output, ct).ConfigureAwait(false);
            probe = await WaitForMsys2Async(requestedCompiler, downloadNetwork, output, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(probe.ActiveMsysRoot))
            {
                throw BuildToolchainException(
                    probe.SelectedCompiler,
                    downloadNetwork,
                    "MSYS2 installation could not be verified.");
            }
        }

        var shell = CreateShellEnvironment(probe.ActiveMsysRoot, probe.SelectedCompiler);
        var hasPrerequisites = await HasBuildPrerequisitesAsync(shell, probe.SelectedCompiler, downloadNetwork, output, ct)
            .ConfigureAwait(false);

        if (!hasPrerequisites)
        {
            if (!autoInstall)
            {
                throw BuildToolchainException(
                    probe.SelectedCompiler,
                    downloadNetwork,
                    "Required MSYS2 build tools are missing.");
            }

            output?.Report("Installing missing MSYS2 packages for the selected compiler...");
            await InstallRequiredPackagesAsync(shell, probe.SelectedCompiler, downloadNetwork, output, ct).ConfigureAwait(false);

            hasPrerequisites = await HasBuildPrerequisitesAsync(shell, probe.SelectedCompiler, downloadNetwork, output, ct)
                .ConfigureAwait(false);
            if (!hasPrerequisites)
            {
                throw BuildToolchainException(
                    probe.SelectedCompiler,
                    downloadNetwork,
                    "Automatic package installation did not provide all required tools.");
            }

            probe = await ProbeToolchainInternalAsync(requestedCompiler, downloadNetwork, output, ct).ConfigureAwait(false);
            var activeRoot = probe.ActiveMsysRoot ?? shell.MsysRoot;
            shell = CreateShellEnvironment(activeRoot, probe.SelectedCompiler);
        }

        return new PreparedToolchain(shell, probe.SelectedCompiler, probe);
    }
    private async Task<StockfishToolchainProbe> WaitForMsys2Async(
        string requestedCompiler,
        bool downloadNetwork,
        IProgress<string>? output,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MsysInstallProbeAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var probe = await ProbeToolchainInternalAsync(requestedCompiler, downloadNetwork, output: null, ct).ConfigureAwait(false);
            if (probe.IsMsys2Installed && !string.IsNullOrWhiteSpace(probe.ActiveMsysRoot))
            {
                output?.Report($"MSYS2 detected after install: {probe.ActiveMsysRoot}");
                return probe;
            }

            output?.Report($"Waiting for MSYS2 installation to finish... ({attempt}/{MsysInstallProbeAttempts})");
            await Task.Delay(MsysInstallProbeDelay, ct).ConfigureAwait(false);
        }

        return await ProbeToolchainInternalAsync(requestedCompiler, downloadNetwork, output, ct).ConfigureAwait(false);
    }

    private static async Task InstallMsys2Async(
        IProgress<string>? output,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Automatic MSYS2 installation is only supported on Windows.");
        }

        var wingetPath = FindExecutableOnPath("winget.exe");
        if (string.IsNullOrWhiteSpace(wingetPath))
        {
            throw new InvalidOperationException(
                "winget is not available. Install MSYS2 manually from https://www.msys2.org/.");
        }

        output?.Report("Installing MSYS2 via winget...");
        var exitCode = await RunProcessAsync(
            wingetPath,
            [
                "install",
                "--id",
                "MSYS2.MSYS2",
                "--exact",
                "--accept-package-agreements",
                "--accept-source-agreements",
                "--silent"
            ],
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            environment: null,
            output,
            ct).ConfigureAwait(false);

        if (exitCode != 0)
        {
            output?.Report($"winget exited with code {exitCode}. Probing for MSYS2 installation anyway...");
        }
    }

    private async Task<StockfishToolchainProbe> ProbeToolchainInternalAsync(
        string requestedCompiler,
        bool downloadNetwork,
        IProgress<string>? output,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var roots = DiscoverMsysRoots()
            .Select(EvaluateRoot)
            .ToList();

        var recommendedCompiler = DetermineRecommendedCompiler(roots);
        var selectedCompiler = ResolveSelectedCompiler(requestedCompiler, recommendedCompiler);
        var activeRoot = SelectActiveRoot(roots, selectedCompiler, downloadNetwork);

        var isReady = activeRoot != null && IsRootReady(activeRoot, selectedCompiler, downloadNetwork);
        var summary = BuildToolchainSummary(roots, activeRoot, selectedCompiler, recommendedCompiler, downloadNetwork, isReady);

        output?.Report(summary);
        foreach (var root in roots)
        {
            output?.Report($"MSYS2 root: {root.RootPath} | make={ToYesNo(root.HasMake)} | mingw={ToYesNo(root.HasMingwCompiler)} | clang={ToYesNo(root.HasClangCompiler)} | binutils={ToYesNo(root.HasMingwBinutils)} | net={ToYesNo(root.HasNetworkDownloader)}");
        }

        return await Task.FromResult(new StockfishToolchainProbe(
            requestedCompiler,
            selectedCompiler,
            recommendedCompiler,
            activeRoot?.RootPath,
            roots.Count > 0,
            isReady,
            roots,
            summary)).ConfigureAwait(false);
    }

    private static string BuildToolchainSummary(
        IReadOnlyList<StockfishMsysRootStatus> roots,
        StockfishMsysRootStatus? activeRoot,
        string selectedCompiler,
        string recommendedCompiler,
        bool downloadNetwork,
        bool isReady)
    {
        if (roots.Count == 0)
        {
            return $"MSYS2 not found. Recommended compiler: {DescribeCompiler(recommendedCompiler)}.";
        }

        var rootPath = activeRoot?.RootPath ?? roots[0].RootPath;
        if (isReady)
        {
            return $"Toolchain ready at '{rootPath}'. Compiler: {DescribeCompiler(selectedCompiler)}.";
        }

        if (activeRoot == null)
        {
            return $"MSYS2 detected, but no usable root selected. Recommended compiler: {DescribeCompiler(recommendedCompiler)}.";
        }

        var missing = string.Join(", ", GetMissingComponents(activeRoot, selectedCompiler, downloadNetwork));
        return $"Toolchain needs setup at '{rootPath}'. Missing: {missing}.";
    }

    private static IReadOnlyList<string> GetMissingComponents(
        StockfishMsysRootStatus root,
        string compiler,
        bool downloadNetwork)
    {
        var missing = new List<string>();

        if (!root.HasMake)
        {
            missing.Add("make");
        }

        if (!HasCompiler(root, compiler))
        {
            missing.Add(GetCompilerBinary(compiler));
        }

        if (!root.HasMingwBinutils)
        {
            missing.Add("mingw binutils (as/ld)");
        }

        if (downloadNetwork && !root.HasNetworkDownloader)
        {
            missing.Add("wget/curl");
        }

        return missing;
    }
    private static StockfishMsysRootStatus? SelectActiveRoot(
        IReadOnlyList<StockfishMsysRootStatus> roots,
        string compiler,
        bool downloadNetwork)
    {
        return roots
            .OrderByDescending(root => IsRootReady(root, compiler, downloadNetwork))
            .ThenByDescending(root => ScoreRoot(root, compiler, downloadNetwork))
            .ThenBy(root => root.RootPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static int ScoreRoot(
        StockfishMsysRootStatus root,
        string compiler,
        bool downloadNetwork)
    {
        var score = 0;

        if (root.HasMake)
        {
            score += 5;
        }

        if (HasCompiler(root, compiler))
        {
            score += 5;
        }

        if (root.HasMingwBinutils)
        {
            score += 3;
        }

        if (downloadNetwork && root.HasNetworkDownloader)
        {
            score += 1;
        }

        if (root.HasMingwCompiler)
        {
            score += 1;
        }

        if (root.HasClangCompiler)
        {
            score += 1;
        }

        return score;
    }

    private static bool IsRootReady(
        StockfishMsysRootStatus root,
        string compiler,
        bool downloadNetwork)
    {
        return root.HasMake &&
               HasCompiler(root, compiler) &&
               root.HasMingwBinutils &&
               (!downloadNetwork || root.HasNetworkDownloader);
    }

    private static bool HasCompiler(StockfishMsysRootStatus root, string compiler) =>
        compiler.Equals("clang", StringComparison.OrdinalIgnoreCase)
            ? root.HasClangCompiler
            : root.HasMingwCompiler;

    private static string DetermineRecommendedCompiler(IReadOnlyList<StockfishMsysRootStatus> roots)
    {
        if (roots.Any(root => root.HasMake && root.HasMingwCompiler))
        {
            return "mingw";
        }

        if (roots.Any(root => root.HasMake && root.HasClangCompiler))
        {
            return "clang";
        }

        if (roots.Any(root => root.HasMingwCompiler))
        {
            return "mingw";
        }

        if (roots.Any(root => root.HasClangCompiler))
        {
            return "clang";
        }

        return "mingw";
    }

    private static string ResolveSelectedCompiler(string requestedCompiler, string recommendedCompiler) =>
        requestedCompiler.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? recommendedCompiler
            : requestedCompiler;

    private static StockfishMsysRootStatus EvaluateRoot(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        var hasMake = File.Exists(Path.Combine(root, "usr", "bin", "make.exe"));
        var hasMingw = File.Exists(Path.Combine(root, "mingw64", "bin", "g++.exe"));
        var hasClang = File.Exists(Path.Combine(root, "clang64", "bin", "clang++.exe"));
        var hasBinutils =
            File.Exists(Path.Combine(root, "mingw64", "bin", "as.exe")) &&
            File.Exists(Path.Combine(root, "mingw64", "bin", "ld.exe"));
        var hasDownloader =
            File.Exists(Path.Combine(root, "usr", "bin", "wget.exe")) ||
            File.Exists(Path.Combine(root, "usr", "bin", "curl.exe"));

        return new StockfishMsysRootStatus(root, hasMake, hasMingw, hasClang, hasBinutils, hasDownloader);
    }

    private static IReadOnlyList<string> DiscoverMsysRoots()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in PreferredMsysRoots)
        {
            candidates.Add(root);
        }

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MSYS2"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "MSYS2"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MSYS2"));

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                candidates.Add(Path.Combine(drive.RootDirectory.FullName, "msys64"));
            }
            catch
            {
            }
        }

        foreach (var pathEntry in EnumeratePathEntries())
        {
            try
            {
                var bashPath = Path.Combine(pathEntry, "bash.exe");
                if (File.Exists(bashPath) && TryResolveMsysRoot(bashPath, out var root))
                {
                    candidates.Add(root);
                }
            }
            catch
            {
            }
        }

        var validRoots = new List<string>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate.Trim());
            }
            catch
            {
                continue;
            }

            if (!IsMsysRoot(fullPath))
            {
                continue;
            }

            if (!validRoots.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                validRoots.Add(fullPath);
            }
        }

        validRoots.Sort(StringComparer.OrdinalIgnoreCase);
        return validRoots;
    }

    private static IEnumerable<string> EnumeratePathEntries()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var cleaned = entry.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            if (seen.Add(cleaned))
            {
                yield return cleaned;
            }
        }
    }

    private static ShellEnvironment CreateShellEnvironment(string msysRoot, string compiler)
    {
        if (string.IsNullOrWhiteSpace(msysRoot) || !IsMsysRoot(msysRoot))
        {
            throw new InvalidOperationException(
                "MSYS2 root was not found. Install MSYS2 (https://www.msys2.org/) to compile Stockfish.");
        }

        var bashPath = Path.Combine(msysRoot, "usr", "bin", "bash.exe");
        if (!File.Exists(bashPath))
        {
            throw new InvalidOperationException($"MSYS2 bash was not found in '{msysRoot}'.");
        }

        var compilerFolder = ResolveCompilerBinFolder(compiler);
        var compilerBin = Path.Combine(msysRoot, compilerFolder, "bin");
        var usrBin = Path.Combine(msysRoot, "usr", "bin");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = string.Join(
                ";",
                new[] { compilerBin, usrBin }
                    .Where(path => !string.IsNullOrWhiteSpace(path))),
            ["MSYSTEM"] = compiler.Equals("clang", StringComparison.OrdinalIgnoreCase) ? "CLANG64" : "MINGW64",
            ["CHERE_INVOKING"] = "1",
            ["MSYS2_PATH_TYPE"] = "inherit"
        };

        return new ShellEnvironment(bashPath, environment, msysRoot);
    }

    private static async Task<bool> HasBuildPrerequisitesAsync(
        ShellEnvironment shell,
        string compiler,
        bool downloadNetwork,
        IProgress<string>? output,
        CancellationToken ct)
    {
        output?.Report($"Using MSYS2 shell: {shell.BashPath}");

        var compilerBinary = GetCompilerBinary(compiler);
        var compileProbe =
            "tmp_src=$(mktemp /tmp/pgntools_probeXXXX.cpp); " +
            "cat > \"$tmp_src\" <<'EOF'\n" +
            "#include <wchar.h>\n" +
            "#include <wctype.h>\n" +
            "#include <thread>\n" +
            "int main() {\n" +
            "  mbstate_t st{};\n" +
            "  wchar_t out{};\n" +
            "  (void)wctype(\"alpha\");\n" +
            "  (void)wctob(L'A');\n" +
            "  (void)btowc('A');\n" +
            "  (void)mbrtowc(&out, \"A\", 1, &st);\n" +
            "  std::thread t([]{});\n" +
            "  t.join();\n" +
            "  return 0;\n" +
            "}\n" +
            "EOF\n" +
            "tmp_exe=\"${tmp_src%.cpp}.exe\"; " +
            $"{compilerBinary} -static \"$tmp_src\" -o \"$tmp_exe\" >/dev/null 2>&1; " +
            "rc=$?; rm -f \"$tmp_src\" \"$tmp_exe\"; test $rc -eq 0";

        var checkCommand =
            $"set -euo pipefail; command -v make >/dev/null && command -v {compilerBinary} >/dev/null && command -v as >/dev/null && command -v ld >/dev/null && as --version >/dev/null && ld --version >/dev/null && {compileProbe}";
        if (downloadNetwork)
        {
            checkCommand += " && (command -v wget >/dev/null || command -v curl >/dev/null)";
        }

        var exitCode = await RunProcessAsync(
            shell.BashPath,
            ["-lc", checkCommand],
            shell.MsysRoot,
            shell.Environment,
            output,
            ct).ConfigureAwait(false);

        if (exitCode == 0)
        {
            return true;
        }

        output?.Report(
            $"Toolchain check failed. Install command: {BuildInstallHintCommand(compiler, downloadNetwork)}");
        return false;
    }
    private static async Task InstallRequiredPackagesAsync(
        ShellEnvironment shell,
        string compiler,
        bool downloadNetwork,
        IProgress<string>? output,
        CancellationToken ct)
    {
        EnsurePacmanDbNotLocked(shell.MsysRoot, output);

        var packages = BuildRequiredPackageList(compiler, downloadNetwork);
        var packageList = string.Join(" ", packages);
        output?.Report("Refreshing MSYS2 package databases (pacman -Sy)...");
        var syncCommand = "set -euo pipefail; pacman -Sy --noconfirm";
        var syncExitCode = await RunProcessAsync(
            shell.BashPath,
            ["-lc", syncCommand],
            shell.MsysRoot,
            shell.Environment,
            output,
            ct).ConfigureAwait(false);

        if (syncExitCode != 0)
        {
            output?.Report($"pacman -Sy exited with code {syncExitCode}. Proceeding with package install attempt...");
        }

        EnsurePacmanDbNotLocked(shell.MsysRoot, output);
        output?.Report("Installing required MSYS2 toolchain packages (targeted, no full system upgrade)...");
        var targetedInstallCommand = $"set -euo pipefail; pacman -S --noconfirm --needed {packageList}";
        var targetedInstallExitCode = await RunProcessAsync(
            shell.BashPath,
            ["-lc", targetedInstallCommand],
            shell.MsysRoot,
            shell.Environment,
            output,
            ct).ConfigureAwait(false);

        if (targetedInstallExitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Targeted MSYS2 package install failed with exit code {targetedInstallExitCode}. " +
            "Automatic full system upgrade is intentionally skipped to avoid long in-app stalls. " +
            $"Open an MSYS2 shell and run: pacman -Syu --noconfirm && pacman -S --noconfirm --needed {packageList}");
    }

    private static IReadOnlyList<string> BuildRequiredPackageList(string compiler, bool downloadNetwork)
    {
        var packages = new List<string>
        {
            "make",
            "mingw-w64-x86_64-binutils",
            "mingw-w64-x86_64-crt",
            "mingw-w64-x86_64-gcc-libs",
            "mingw-w64-x86_64-gmp",
            "mingw-w64-x86_64-isl",
            "mingw-w64-x86_64-mpc",
            "mingw-w64-x86_64-mpfr",
            "mingw-w64-x86_64-libiconv",
            "mingw-w64-x86_64-zlib",
            "mingw-w64-x86_64-zstd",
            "mingw-w64-x86_64-gettext-runtime",
            "mingw-w64-x86_64-winpthreads",
            GetCompilerPackage(compiler)
        };

        if (downloadNetwork)
        {
            packages.Add("wget");
            packages.Add("curl");
        }

        return packages;
    }

    private static string BuildInstallHintCommand(string compiler, bool downloadNetwork)
    {
        var packages = BuildRequiredPackageList(compiler, downloadNetwork);
        return $"pacman -Sy --noconfirm && pacman -S --noconfirm --needed {string.Join(" ", packages)}";
    }

    private static string BuildCompatibilityMakeArgs(string sourceRef, string compiler) =>
        RequiresNnueEmbeddingWorkaround(sourceRef, compiler)
            ? " ENV_CXXFLAGS='-DNNUE_EMBEDDING_OFF'"
            : string.Empty;

    private static string ResolveStockfishVersionSegment(string sourceRef)
    {
        if (sourceRef.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            return "dev";
        }

        if (sourceRef.StartsWith("sf_", StringComparison.OrdinalIgnoreCase))
        {
            return SanitizeFileSegment(sourceRef[3..].ToLowerInvariant());
        }

        return SanitizeFileSegment(sourceRef.ToLowerInvariant());
    }

    private static bool RequiresNnueEmbeddingWorkaround(string sourceRef, string compiler) =>
        compiler.Equals("mingw", StringComparison.OrdinalIgnoreCase) &&
        sourceRef.Equals("sf_17", StringComparison.OrdinalIgnoreCase);

    private static InvalidOperationException BuildToolchainException(
        string compiler,
        bool downloadNetwork,
        string reason)
    {
        var installHint = BuildInstallHintCommand(compiler, downloadNetwork);
        return new InvalidOperationException(
            $"{reason} Open an MSYS2 shell and run: {installHint}");
    }

    private static string GetCompilerPackage(string compiler) =>
        compiler.Equals("clang", StringComparison.OrdinalIgnoreCase)
            ? "mingw-w64-clang-x86_64-clang"
            : "mingw-w64-x86_64-gcc";

    private static string GetCompilerBinary(string compiler) =>
        compiler.Equals("clang", StringComparison.OrdinalIgnoreCase)
            ? "clang++"
            : "x86_64-w64-mingw32-c++";

    private static string GetCompilerCxxOverride(string compiler) =>
        GetCompilerBinary(compiler);

    private static string DescribeCompiler(string compiler) =>
        compiler.Equals("clang", StringComparison.OrdinalIgnoreCase)
            ? "clang (LLVM)"
            : "mingw (GCC)";

    private static string ToYesNo(bool value) => value ? "yes" : "no";

    private static string NormalizeCompilerSelection(string compiler)
    {
        var normalized = string.IsNullOrWhiteSpace(compiler)
            ? "auto"
            : compiler.Trim().ToLowerInvariant();

        return normalized switch
        {
            "auto" => "auto",
            "mingw" => "mingw",
            "gcc" => "mingw",
            "clang" => "clang",
            _ => throw new ArgumentException(
                "Compiler must be one of: auto, mingw, clang.",
                nameof(compiler))
        };
    }

    private static async Task CloneRepositoryAsync(
        string workingDirectory,
        string repositoryPath,
        string sourceRef,
        string? gitHubPat,
        IProgress<string>? output,
        CancellationToken ct)
    {
        var shallowArgs = BuildCloneArguments(repositoryPath, sourceRef, gitHubPat, shallow: true);
        var shallowExitCode = await RunProcessAsync(
            "git",
            shallowArgs,
            workingDirectory,
            environment: null,
            output,
            ct).ConfigureAwait(false);

        if (shallowExitCode == 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(gitHubPat))
        {
            output?.Report("Authenticated clone failed. Retrying anonymous clone...");
            SafeDeleteDirectory(repositoryPath);

            var anonymousShallowArgs = BuildCloneArguments(repositoryPath, sourceRef, gitHubPat: null, shallow: true);
            var anonymousShallowExitCode = await RunProcessAsync(
                "git",
                anonymousShallowArgs,
                workingDirectory,
                environment: null,
                output,
                ct).ConfigureAwait(false);

            if (anonymousShallowExitCode == 0)
            {
                return;
            }
        }

        output?.Report("Shallow clone failed. Retrying with full history...");
        SafeDeleteDirectory(repositoryPath);

        var fullArgs = BuildCloneArguments(repositoryPath, sourceRef, gitHubPat: null, shallow: false);
        var fullExitCode = await RunProcessAsync(
            "git",
            fullArgs,
            workingDirectory,
            environment: null,
            output,
            ct).ConfigureAwait(false);

        if (fullExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to clone Stockfish repository for ref '{sourceRef}'. " +
                "If you entered a PAT, verify it has GitHub repo read access; for Stockfish you can also leave PAT empty. " +
                "See build output for details.");
        }
    }

    private static List<string> BuildCloneArguments(
        string repositoryPath,
        string sourceRef,
        string? gitHubPat,
        bool shallow)
    {
        var args = new List<string>
        {
            "-c",
            "credential.helper=",
            "-c",
            "core.askPass=",
            "-c",
            "credential.interactive=never",
            "-c",
            "http.extraheader="
        };

        if (!string.IsNullOrWhiteSpace(gitHubPat))
        {
            args.Add("-c");
            args.Add($"http.extraheader=AUTHORIZATION: Basic {BuildGitHubBasicCredential(gitHubPat)}");
        }

        args.Add("clone");
        if (shallow)
        {
            args.Add("--depth");
            args.Add("1");
        }

        args.Add("--branch");
        args.Add(sourceRef);
        args.Add(StockfishRepository.ToString());
        args.Add(repositoryPath);

        return args;
    }

    private static string BuildGitHubBasicCredential(string gitHubPat)
    {
        var token = gitHubPat.Trim();
        var bytes = Encoding.UTF8.GetBytes($"x-access-token:{token}");
        return Convert.ToBase64String(bytes);
    }

    private static async Task RunBashCommandAsync(
        ShellEnvironment shell,
        string workingDirectory,
        string command,
        IProgress<string>? output,
        CancellationToken ct)
    {
        output?.Report($"Running in {workingDirectory}: {command}");
        var arguments = new List<string> { "-lc", command };
        var exitCode = await RunProcessAsync(
            shell.BashPath,
            arguments,
            workingDirectory,
            shell.Environment,
            output,
            ct).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Command failed with exit code {exitCode}: {command}");
        }
    }

    private static async Task TryRunBashCommandAsync(
        ShellEnvironment shell,
        string workingDirectory,
        string command,
        IProgress<string>? output,
        CancellationToken ct)
    {
        try
        {
            await RunBashCommandAsync(shell, workingDirectory, command, output, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            output?.Report($"Non-fatal command failure ignored: {ex.Message}");
        }
    }

    private static bool TryResolveMsysRoot(string bashPath, out string msysRoot)
    {
        msysRoot = string.Empty;
        try
        {
            var bashDirectory = Path.GetDirectoryName(bashPath);
            if (string.IsNullOrWhiteSpace(bashDirectory))
            {
                return false;
            }

            var bashDirInfo = new DirectoryInfo(bashDirectory);
            string? root;

            if (bashDirInfo.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                bashDirInfo.Parent?.Name.Equals("usr", StringComparison.OrdinalIgnoreCase) == true)
            {
                root = bashDirInfo.Parent.Parent?.FullName;
            }
            else if (bashDirInfo.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
            {
                root = bashDirInfo.Parent?.FullName;
            }
            else
            {
                root = null;
            }

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return false;
            }

            msysRoot = root;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveCompilerBinFolder(string compiler) =>
        compiler.Equals("clang", StringComparison.OrdinalIgnoreCase) ? "clang64" : "mingw64";

    private static string? FindExecutableOnPath(string fileName)
    {
        foreach (var directory in EnumeratePathEntries())
        {
            try
            {
                var fullPath = Path.Combine(directory, fileName);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static bool IsMsysRoot(string root) =>
        File.Exists(Path.Combine(root, "usr", "bin", "bash.exe")) &&
        File.Exists(Path.Combine(root, "usr", "bin", "pacman.exe"));

    private static void EnsurePacmanDbNotLocked(string msysRoot, IProgress<string>? output = null)
    {
        var lockFile = Path.Combine(msysRoot, "var", "lib", "pacman", "db.lck");
        if (!File.Exists(lockFile))
        {
            return;
        }

        var activePacmanProcesses = FindActivePacmanProcesses();
        if (activePacmanProcesses.Count > 0)
        {
            throw new InvalidOperationException(
                $"MSYS2 package database is locked: {lockFile}. " +
                $"Active pacman process(es): {string.Join(", ", activePacmanProcesses)}. " +
                "Wait for package operations to finish, then retry.");
        }

        try
        {
            File.Delete(lockFile);
            output?.Report($"Removed stale MSYS2 pacman lock file: {lockFile}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"MSYS2 package database is locked: {lockFile}. " +
                "No active pacman process was detected, but the lock file could not be removed automatically. " +
                "Close MSYS2 shells and remove the lock file manually, then retry.",
                ex);
        }
    }

    private static IReadOnlyList<string> FindActivePacmanProcesses()
    {
        var active = new List<string>();
        var processes = Process.GetProcesses();

        foreach (var process in processes)
        {
            try
            {
                if (!process.ProcessName.StartsWith("pacman", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                active.Add($"{process.ProcessName} (pid {process.Id})");
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return active;
    }

    private static string LocateBuiltBinary(string sourceDirectory)
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { "stockfish.exe", "stockfish" }
            : new[] { "stockfish", "stockfish.exe" };

        foreach (var fileName in candidates)
        {
            var candidate = Path.Combine(sourceDirectory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var fallback = Directory.EnumerateFiles(sourceDirectory, "stockfish*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(fallback))
        {
            return fallback;
        }

        throw new FileNotFoundException(
            $"Stockfish binary was not found in '{sourceDirectory}' after build.");
    }

    private static string SanitizeFileSegment(string value)
    {
        var sanitized = Regex.Replace(value, @"[^A-Za-z0-9._-]", "_");
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }

    private static string NormalizeWorkspace(string workspaceFolder)
    {
        if (string.IsNullOrWhiteSpace(workspaceFolder))
        {
            throw new ArgumentException("Workspace folder is required.", nameof(workspaceFolder));
        }

        return Path.GetFullPath(workspaceFolder.Trim());
    }

    private static string NormalizeSourceRef(string sourceRef)
    {
        var normalized = string.IsNullOrWhiteSpace(sourceRef) ? "master" : sourceRef.Trim();
        if (!SourceRefRegex().IsMatch(normalized))
        {
            throw new ArgumentException("Source ref contains invalid characters.", nameof(sourceRef));
        }

        return normalized;
    }

    private static string NormalizeBuildToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }

        var normalized = value.Trim();
        if (!BuildTokenRegex().IsMatch(normalized))
        {
            throw new ArgumentException($"{parameterName} contains invalid characters.", parameterName);
        }

        return normalized;
    }

    private static async Task<int> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        IProgress<string>? output,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment != null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        output?.Report($"Executing: {fileName} {FormatArguments(arguments)}");
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            throw new InvalidOperationException(
                $"Required tool '{fileName}' was not found. Ensure it is installed and available on PATH.",
                ex);
        }

        using var registration = ct.Register(() => TryKillProcess(process));
        var stdoutTask = PumpReaderAsync(process.StandardOutput, output, ct);
        var stderrTask = PumpReaderAsync(process.StandardError, output, ct);

        await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(ct)).ConfigureAwait(false);
        return process.ExitCode;
    }

    private static async Task PumpReaderAsync(
        StreamReader reader,
        IProgress<string>? output,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            output?.Report(line);
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (PlatformNotSupportedException)
        {
            try
            {
                process.Kill();
            }
            catch
            {
            }
        }
        catch
        {
        }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string FormatArguments(IReadOnlyList<string> arguments) =>
        string.Join(" ", arguments.Select(QuoteArgument));

    private static string QuoteArgument(string value)
    {
        value = SanitizeArgumentForLogging(value);
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static string SanitizeArgumentForLogging(string value)
    {
        const string gitHubAuthPrefix = "http.extraheader=AUTHORIZATION: Basic ";
        return value.StartsWith(gitHubAuthPrefix, StringComparison.OrdinalIgnoreCase)
            ? $"{gitHubAuthPrefix}***"
            : value;
    }

    [GeneratedRegex("^[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SourceRefRegex();

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex BuildTokenRegex();

    private sealed record ShellEnvironment(
        string BashPath,
        IReadOnlyDictionary<string, string> Environment,
        string MsysRoot);

    private sealed record PreparedToolchain(
        ShellEnvironment Shell,
        string Compiler,
        StockfishToolchainProbe Probe);
}
