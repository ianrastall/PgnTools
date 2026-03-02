using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PgnTools.Services;

public interface IBerserkCompilerService
{
    Task<StockfishCompileResult> CompileAsync(
        StockfishCompileOptions options,
        IProgress<StockfishCompileProgress>? progress = null,
        IProgress<string>? output = null,
        CancellationToken ct = default);
}

public sealed partial class BerserkCompilerService(IStockfishCompilerService stockfishCompilerService) : IBerserkCompilerService
{
    private static readonly Uri BerserkRepository = new("https://github.com/jhonnold/berserk.git");

    private readonly IStockfishCompilerService _stockfishCompilerService = stockfishCompilerService;

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
        var buildRoot = Path.Combine(workspaceFolder, $"berserk-build-{stamp}");
        var repositoryPath = Path.Combine(buildRoot, "berserk");
        Directory.CreateDirectory(buildRoot);

        output?.Report($"Workspace: {workspaceFolder}");
        output?.Report($"Build directory: {buildRoot}");

        progress?.Report(new StockfishCompileProgress(
            StockfishCompileStage.Toolchain,
            "Detecting compiler toolchain...",
            12));

        var probe = await _stockfishCompilerService
            .ProbeToolchainAsync(requestedCompiler, downloadNetwork: true, output, ct)
            .ConfigureAwait(false);

        if (!probe.IsReadyForSelection || string.IsNullOrWhiteSpace(probe.ActiveMsysRoot))
        {
            if (!options.AutoInstallToolchain)
            {
                throw new InvalidOperationException(
                    "Compiler toolchain is not ready. Run toolchain install/repair first.");
            }

            probe = await _stockfishCompilerService
                .InstallOrRepairToolchainAsync(requestedCompiler, downloadNetwork: true, output, ct)
                .ConfigureAwait(false);
        }

        if (!probe.IsReadyForSelection || string.IsNullOrWhiteSpace(probe.ActiveMsysRoot))
        {
            throw new InvalidOperationException("Toolchain is still not ready for Berserk compilation.");
        }

        var selectedCompiler = probe.SelectedCompiler;
        var makefileGeneration = ClassifyMakefileGeneration(sourceRef);
        var shell = CreateShellEnvironment(probe.ActiveMsysRoot, selectedCompiler);
        var cc = selectedCompiler.Equals("clang", StringComparison.OrdinalIgnoreCase) ? "clang" : "gcc";
        var berserkArch = MapBerserkArchitecture(architecture);
        var includeSubmodules = IsNetworksSubmoduleEra(sourceRef);
        var compileAttempts = BuildCompileAttempts(
            options.BuildTarget,
            berserkArch,
            cc,
            jobs,
            options.DownloadNetwork,
            makefileGeneration);

        output?.Report(
            $"Build settings: target={options.BuildTarget}, arch={berserkArch}, compiler={selectedCompiler}, jobs={jobs}, net={options.DownloadNetwork}, strip={options.StripExecutable}");
        output?.Report($"MSYS2 root: {shell.MsysRoot}");
        if (includeSubmodules)
        {
            output?.Report("This Berserk version uses git submodules for networks; cloning recursively.");
        }
        else if (makefileGeneration == BerserkMakefileGeneration.DownloadNetworks)
        {
            output?.Report("This Berserk version uses makefile-driven network download/validation.");
        }

        progress?.Report(new StockfishCompileProgress(
            StockfishCompileStage.Cloning,
            $"Cloning Berserk ({sourceRef})...",
            20));

        await CloneRepositoryAsync(
                buildRoot,
                repositoryPath,
                sourceRef,
                options.GitHubPat,
                includeSubmodules,
                output,
                ct)
            .ConfigureAwait(false);

        var sourceDirectory = Path.Combine(repositoryPath, "src");
        if (!Directory.Exists(sourceDirectory))
        {
            throw new InvalidOperationException($"Berserk source directory not found: {sourceDirectory}");
        }

        if (includeSubmodules)
        {
            await RunBashCommandAsync(
                    shell,
                    repositoryPath,
                    "set -euo pipefail; git submodule update --init --recursive",
                    output,
                    ct)
                .ConfigureAwait(false);
        }

        if (!options.DownloadNetwork &&
            makefileGeneration == BerserkMakefileGeneration.DownloadNetworks)
        {
            output?.Report(
                "Download network is disabled. Build will avoid the network-prefetch target when possible.");
        }

        progress?.Report(new StockfishCompileProgress(
            StockfishCompileStage.Building,
            $"Compiling Berserk ({compileAttempts[0].Target})...",
            65));

        var effectiveBuildTarget = options.BuildTarget;
        Exception? lastBuildError = null;
        var buildSucceeded = false;

        for (var i = 0; i < compileAttempts.Count; i++)
        {
            var attempt = compileAttempts[i];

            try
            {
                if (i > 0)
                {
                    output?.Report(
                        $"Retrying build with target '{attempt.Target}' after previous failure...");
                }

                await RunBashCommandAsync(shell, sourceDirectory, attempt.Command, output, ct).ConfigureAwait(false);
                effectiveBuildTarget = attempt.EffectiveBuildTarget;
                buildSucceeded = true;
                break;
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
            {
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                lastBuildError = ex;
                output?.Report($"Berserk make target '{attempt.Target}' failed: {ex.Message}");

                await TryRunBashCommandAsync(
                        shell,
                        sourceDirectory,
                        "set -euo pipefail; make clean",
                        output,
                        ct)
                    .ConfigureAwait(false);
            }
        }

        if (!buildSucceeded)
        {
            throw new InvalidOperationException(
                $"Failed to build Berserk for ref '{sourceRef}'. Last error: {lastBuildError?.Message}",
                lastBuildError);
        }

        if (options.StripExecutable)
        {
            progress?.Report(new StockfishCompileProgress(
                StockfishCompileStage.Stripping,
                "Stripping executable...",
                85));

            await TryRunBashCommandAsync(
                    shell,
                    sourceDirectory,
                    $"set -euo pipefail; make strip ARCH={berserkArch} CC={cc}",
                    output,
                    ct)
                .ConfigureAwait(false);
        }

        progress?.Report(new StockfishCompileProgress(
            StockfishCompileStage.Finalizing,
            "Finalizing compiled binary...",
            95));

        var builtBinaryPath = LocateBuiltBinary(sourceDirectory);
        var versionSegment = ResolveBerserkVersionSegment(sourceRef);
        var versionDirectory = EnginePackageLayout.BuildVersionDirectory(
            workspaceFolder,
            "berserk",
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

        await TryRunBashCommandAsync(shell, sourceDirectory, "set -euo pipefail; make clean", output, ct)
            .ConfigureAwait(false);

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
            selectedCompiler,
            effectiveBuildTarget);
    }

    private static IReadOnlyList<BuildAttempt> BuildCompileAttempts(
        StockfishBuildTarget buildTarget,
        string architecture,
        string cc,
        int jobs,
        bool downloadNetwork,
        BerserkMakefileGeneration makefileGeneration)
    {
        var attempts = new List<BuildAttempt>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddTarget(string target, StockfishBuildTarget effectiveBuildTarget)
        {
            if (string.IsNullOrWhiteSpace(target) || !seenTargets.Add(target))
            {
                return;
            }

            attempts.Add(new BuildAttempt(
                target,
                BuildCompileCommand(target, architecture, cc, jobs),
                effectiveBuildTarget));
        }

        var standardTarget = ResolveStandardBuildTarget(makefileGeneration, downloadNetwork);
        if (buildTarget == StockfishBuildTarget.ProfileBuild)
        {
            AddTarget("pgo", StockfishBuildTarget.ProfileBuild);
            AddTarget(standardTarget, StockfishBuildTarget.Build);
        }
        else
        {
            AddTarget(standardTarget, StockfishBuildTarget.Build);
        }

        AddTarget("build", StockfishBuildTarget.Build);
        AddTarget("basic", StockfishBuildTarget.Build);
        AddTarget("all", StockfishBuildTarget.Build);

        return attempts;
    }

    private static string BuildCompileCommand(
        string target,
        string architecture,
        string cc,
        int jobs) =>
        $"set -euo pipefail; make -j {jobs} {target} ARCH={architecture} CC={cc}";

    private static string ResolveStandardBuildTarget(
        BerserkMakefileGeneration makefileGeneration,
        bool downloadNetwork) =>
        makefileGeneration switch
        {
            BerserkMakefileGeneration.DownloadNetworks => downloadNetwork ? "build" : "all",
            BerserkMakefileGeneration.SubmoduleNetworks => "basic",
            _ => "all"
        };

    private static BerserkMakefileGeneration ClassifyMakefileGeneration(string sourceRef)
    {
        if (IsNetworksSubmoduleEra(sourceRef))
        {
            return BerserkMakefileGeneration.SubmoduleNetworks;
        }

        if (IsDownloadNetworkEra(sourceRef))
        {
            return BerserkMakefileGeneration.DownloadNetworks;
        }

        if (TryParseBerserkVersion(sourceRef, out var version) && version.Major >= 6)
        {
            return BerserkMakefileGeneration.EvalFileLegacy;
        }

        return BerserkMakefileGeneration.Classic;
    }

    private static bool IsDownloadNetworkEra(string sourceRef) =>
        sourceRef.Equals("main", StringComparison.OrdinalIgnoreCase) ||
        sourceRef.Equals("master", StringComparison.OrdinalIgnoreCase) ||
        (TryParseBerserkVersion(sourceRef, out var version) && version.Major >= 13);

    private static bool IsNetworksSubmoduleEra(string sourceRef) =>
        sourceRef.Equals("9", StringComparison.OrdinalIgnoreCase) ||
        sourceRef.Equals("10", StringComparison.OrdinalIgnoreCase) ||
        sourceRef.Equals("11", StringComparison.OrdinalIgnoreCase) ||
        sourceRef.Equals("11.1", StringComparison.OrdinalIgnoreCase) ||
        sourceRef.Equals("12", StringComparison.OrdinalIgnoreCase) ||
        sourceRef.Equals("12.1", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseBerserkVersion(string sourceRef, out BerserkVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(sourceRef))
        {
            return false;
        }

        var normalized = sourceRef.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length is < 1 or > 3)
        {
            return false;
        }

        var parsedSegments = new int[3];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsedSegments[i]))
            {
                return false;
            }
        }

        version = new BerserkVersion(parsedSegments[0], parsedSegments[1], parsedSegments[2]);
        return true;
    }

    private static string ResolveBerserkVersionSegment(string sourceRef)
    {
        if (sourceRef.Equals("main", StringComparison.OrdinalIgnoreCase) ||
            sourceRef.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            return "dev";
        }

        var normalized = sourceRef.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        return SanitizeFileSegment(normalized.ToLowerInvariant());
    }

    private static string MapBerserkArchitecture(string stockfishArchitecture)
    {
        var arch = stockfishArchitecture.Trim().ToLowerInvariant();
        return arch switch
        {
            "native" => "native",
            "x86-64-avx512icl" => "avx512",
            "x86-64-vnni512" => "avx512",
            "x86-64-avx512" => "avx512",
            "x86-64-avxvnni" => "avx2",
            "x86-64-bmi2" => "avx2",
            "x86-64-avx2" => "avx2",
            "x86-64-sse41-popcnt" => "sse41",
            "x86-64-ssse3" => "ssse3",
            "x86-64-sse3-popcnt" => "x86-64",
            "x86-64" => "x86-64",
            _ => "x86-64"
        };
    }

    private static ShellEnvironment CreateShellEnvironment(string msysRoot, string compiler)
    {
        var bashPath = Path.Combine(msysRoot, "usr", "bin", "bash.exe");
        if (!File.Exists(bashPath))
        {
            throw new InvalidOperationException($"MSYS2 bash was not found in '{msysRoot}'.");
        }

        var compilerFolder = compiler.Equals("clang", StringComparison.OrdinalIgnoreCase) ? "clang64" : "mingw64";
        var compilerBin = Path.Combine(msysRoot, compilerFolder, "bin");
        var usrBin = Path.Combine(msysRoot, "usr", "bin");

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = string.Join(";", new[] { compilerBin, usrBin }),
            ["MSYSTEM"] = compiler.Equals("clang", StringComparison.OrdinalIgnoreCase) ? "CLANG64" : "MINGW64",
            ["CHERE_INVOKING"] = "1",
            ["MSYS2_PATH_TYPE"] = "inherit"
        };

        return new ShellEnvironment(bashPath, environment, msysRoot);
    }

    private static async Task CloneRepositoryAsync(
        string workingDirectory,
        string repositoryPath,
        string sourceRef,
        string? gitHubPat,
        bool includeSubmodules,
        IProgress<string>? output,
        CancellationToken ct)
    {
        var cloneArgs = BuildCloneArguments(repositoryPath, sourceRef, gitHubPat, includeSubmodules, shallow: true);
        var shallowExitCode = await RunProcessAsync(
            "git",
            cloneArgs,
            workingDirectory,
            environment: null,
            output,
            ct).ConfigureAwait(false);

        if (shallowExitCode == 0)
        {
            return;
        }

        output?.Report("Shallow clone failed. Retrying with full history...");
        SafeDeleteDirectory(repositoryPath);

        var fullArgs = BuildCloneArguments(repositoryPath, sourceRef, gitHubPat, includeSubmodules, shallow: false);
        var fullExitCode = await RunProcessAsync(
            "git",
            fullArgs,
            workingDirectory,
            environment: null,
            output,
            ct).ConfigureAwait(false);

        if (fullExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to clone Berserk repository for ref '{sourceRef}'.");
        }
    }

    private static List<string> BuildCloneArguments(
        string repositoryPath,
        string sourceRef,
        string? gitHubPat,
        bool includeSubmodules,
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
        if (includeSubmodules)
        {
            args.Add("--recursive");
        }

        if (shallow)
        {
            args.Add("--depth");
            args.Add("1");
        }

        args.Add("--branch");
        args.Add(sourceRef);
        args.Add(BerserkRepository.ToString());
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
        var exitCode = await RunProcessAsync(
            shell.BashPath,
            ["-lc", command],
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

    private static string LocateBuiltBinary(string sourceDirectory)
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { "berserk.exe", "berserk" }
            : new[] { "berserk", "berserk.exe" };

        foreach (var fileName in candidates)
        {
            var path = Path.Combine(sourceDirectory, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        var recursiveExe = Directory
            .EnumerateFiles(sourceDirectory, "berserk*.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(recursiveExe))
        {
            return recursiveExe;
        }

        var recursiveNoExtension = Directory
            .EnumerateFiles(sourceDirectory, "berserk*", SearchOption.AllDirectories)
            .Where(path => string.IsNullOrEmpty(Path.GetExtension(path)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(recursiveNoExtension))
        {
            return recursiveNoExtension;
        }

        var topLevelFallback = Directory
            .EnumerateFiles(sourceDirectory, "berserk*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(topLevelFallback))
        {
            return topLevelFallback;
        }

        throw new FileNotFoundException($"Berserk binary was not found in '{sourceDirectory}' after build.");
    }

    private static string SanitizeFileSegment(string value)
    {
        var sanitized = Regex.Replace(value, @"[^A-Za-z0-9._-]", "_");
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
    }

    private static string NormalizeSourceRef(string sourceRef)
    {
        var normalized = string.IsNullOrWhiteSpace(sourceRef) ? "main" : sourceRef.Trim();
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

    private static string NormalizeWorkspace(string workspaceFolder)
    {
        if (string.IsNullOrWhiteSpace(workspaceFolder))
        {
            throw new ArgumentException("Workspace folder is required.", nameof(workspaceFolder));
        }

        return Path.GetFullPath(workspaceFolder.Trim());
    }

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
            _ => throw new ArgumentException("Compiler must be one of: auto, mingw, clang.", nameof(compiler))
        };
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
            if (line == null)
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
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
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

    private readonly record struct BuildAttempt(
        string Target,
        string Command,
        StockfishBuildTarget EffectiveBuildTarget);

    private readonly record struct BerserkVersion(int Major, int Minor, int Patch);

    private enum BerserkMakefileGeneration
    {
        Classic,
        EvalFileLegacy,
        SubmoduleNetworks,
        DownloadNetworks
    }

    private sealed record ShellEnvironment(
        string BashPath,
        IReadOnlyDictionary<string, string> Environment,
        string MsysRoot);
}
