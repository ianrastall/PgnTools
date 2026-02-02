using System.Text.Json;

namespace PgnTools.Services;

public sealed record EleganceGoldenCaseResult(
    string Name,
    string InputFilePath,
    double Score,
    bool Passed,
    string Message);

public sealed record EleganceGoldenValidationSummary(
    int Total,
    int Passed,
    IReadOnlyList<EleganceGoldenCaseResult> Cases);

public interface IEleganceGoldenValidationService
{
    Task<EleganceGoldenValidationSummary> ValidateAsync(
        string manifestPath,
        string enginePath,
        int depth,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs regression checks for known "golden" games against expected Elegance score ranges.
/// </summary>
public sealed class EleganceGoldenValidationService : IEleganceGoldenValidationService
{
    private readonly IEleganceService _eleganceService;

    public EleganceGoldenValidationService(IEleganceService eleganceService)
    {
        _eleganceService = eleganceService;
    }

    public async Task<EleganceGoldenValidationSummary> ValidateAsync(
        string manifestPath,
        string enginePath,
        int depth,
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new ArgumentException("Manifest path is required.", nameof(manifestPath));
        }

        if (string.IsNullOrWhiteSpace(enginePath))
        {
            throw new ArgumentException("Engine path is required.", nameof(enginePath));
        }

        if (depth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), "Depth must be greater than zero.");
        }

        var manifestFullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(manifestFullPath))
        {
            throw new FileNotFoundException("Golden validation manifest not found.", manifestFullPath);
        }

        var manifest = await LoadManifestAsync(manifestFullPath, cancellationToken).ConfigureAwait(false);
        if (manifest.Cases.Count == 0)
        {
            return new EleganceGoldenValidationSummary(0, 0, []);
        }

        var results = new List<EleganceGoldenCaseResult>(manifest.Cases.Count);
        var manifestDirectory = Path.GetDirectoryName(manifestFullPath) ?? Directory.GetCurrentDirectory();

        for (var i = 0; i < manifest.Cases.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var golden = manifest.Cases[i];

            status?.Report($"Validating {i + 1}/{manifest.Cases.Count}: {golden.Name}");

            var inputPath = ResolveInputPath(manifestDirectory, golden.InputFilePath);
            if (!File.Exists(inputPath))
            {
                results.Add(new EleganceGoldenCaseResult(
                    golden.Name,
                    inputPath,
                    0,
                    false,
                    "Input PGN not found."));
                continue;
            }

            var tempOutput = Path.Combine(Path.GetTempPath(), $"pgntools-elegance-golden-{Guid.NewGuid():N}.pgn");

            try
            {
                var result = await _eleganceService.TagEleganceAsync(
                    inputPath,
                    tempOutput,
                    enginePath,
                    depth,
                    progress: null,
                    cancellationToken).ConfigureAwait(false);

                if (result.ProcessedGames == 0)
                {
                    results.Add(new EleganceGoldenCaseResult(
                        golden.Name,
                        inputPath,
                        0,
                        false,
                        "No games were processed."));
                    continue;
                }

                var score = result.AverageScore;
                var passed = (!golden.MinScore.HasValue || score >= golden.MinScore.Value) &&
                             (!golden.MaxScore.HasValue || score <= golden.MaxScore.Value);

                var minText = golden.MinScore.HasValue ? golden.MinScore.Value.ToString("0.##") : "-inf";
                var maxText = golden.MaxScore.HasValue ? golden.MaxScore.Value.ToString("0.##") : "+inf";
                var message = passed
                    ? $"Score {score:0.##} within expected range [{minText}, {maxText}]"
                    : $"Score {score:0.##} outside expected range [{minText}, {maxText}]";

                results.Add(new EleganceGoldenCaseResult(
                    golden.Name,
                    inputPath,
                    score,
                    passed,
                    message));
            }
            finally
            {
                if (File.Exists(tempOutput))
                {
                    try
                    {
                        File.Delete(tempOutput);
                    }
                    catch
                    {
                    }
                }
            }
        }

        var passedCount = results.Count(r => r.Passed);
        return new EleganceGoldenValidationSummary(results.Count, passedCount, results);
    }

    private static async Task<GoldenManifest> LoadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<GoldenManifest>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return manifest ?? new GoldenManifest();
    }

    private static string ResolveInputPath(string manifestDirectory, string inputPath)
    {
        if (Path.IsPathRooted(inputPath))
        {
            return Path.GetFullPath(inputPath);
        }

        return Path.GetFullPath(Path.Combine(manifestDirectory, inputPath));
    }

    private sealed class GoldenManifest
    {
        public List<GoldenCase> Cases { get; set; } = [];
    }

    private sealed class GoldenCase
    {
        public string Name { get; set; } = "Unnamed Golden";
        public string InputFilePath { get; set; } = string.Empty;
        public double? MinScore { get; set; }
        public double? MaxScore { get; set; }
    }
}
