using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;

namespace PgnTools.Services;

public sealed record StockfishDownloadResult(
    string Tag,
    string Variant,
    string InstallDirectory,
    string ExecutablePath);

public interface IStockfishDownloaderService
{
    StockfishVariant SelectBestVariant();

    Task<StockfishDownloadResult> DownloadLatestAsync(
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default);
}

public enum StockfishVariant
{
    Base,
    Sse41Popcnt,
    Bmi2,
    Avx2,
    Avx512,
    Vnni256,
    Vnni512
}

/// <summary>
/// Downloads the latest Stockfish release from the official GitHub repository,
/// selecting a suitable variant for the current CPU.
/// </summary>
public sealed class StockfishDownloaderService : IStockfishDownloaderService
{
    private const int BufferSize = 65536;
    private const int MaxRetryAttempts = 4;
    private static readonly TimeSpan ReleaseRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadRequestTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2)
    ];
    private static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/official-stockfish/Stockfish/releases/latest");

    private static readonly HttpClient HttpClient = CreateClient();

    public StockfishVariant SelectBestVariant()
    {
        // Prefer the most capable build available on this CPU.
        // Order mirrors the release asset variants.
        if (IsVnni512Supported())
        {
            return StockfishVariant.Vnni512;
        }

        if (IsVnni256Supported())
        {
            return StockfishVariant.Vnni256;
        }

        if (IsAvx512Supported())
        {
            return StockfishVariant.Avx512;
        }

        if (Avx2.IsSupported)
        {
            return StockfishVariant.Avx2;
        }

        if (Bmi2.IsSupported)
        {
            return StockfishVariant.Bmi2;
        }

        if (Sse41.IsSupported && Popcnt.IsSupported)
        {
            return StockfishVariant.Sse41Popcnt;
        }

        return StockfishVariant.Base;
    }

    public async Task<StockfishDownloadResult> DownloadLatestAsync(
        IProgress<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        status?.Report("Querying latest Stockfish release...");

        using var response = await SendWithRetryAsync(
            () => CreateGitHubRequest(LatestReleaseUri, expectsJson: true),
            ReleaseRequestTimeout,
            cancellationToken);

        if (IsRateLimited(response))
        {
            throw new InvalidOperationException(
                "GitHub API rate limit exceeded. Set PGNTOOLS_GITHUB_TOKEN or GITHUB_TOKEN to increase the limit.");
        }

        response.EnsureSuccessStatusCode();

        await using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "latest";
        var assets = root.GetProperty("assets");

        var variant = SelectBestVariant();
        var asset = SelectAsset(assets, variant);
        if (asset is null)
        {
            throw new InvalidOperationException("No compatible Stockfish Windows asset was found in the latest release.");
        }

        var assetName = asset.Name;
        var downloadUrl = !string.IsNullOrWhiteSpace(asset.BrowserUrl)
            ? asset.BrowserUrl
            : asset.ApiUrl;

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new InvalidOperationException($"Download URL not found for asset '{assetName}'.");
        }

        var installDirectory = GetInstallDirectory(tag, variant);
        if (Directory.Exists(installDirectory))
        {
            Directory.Delete(installDirectory, recursive: true);
        }

        Directory.CreateDirectory(installDirectory);

        var tempZipPath = Path.Combine(Path.GetTempPath(), $"stockfish_{tag}_{Guid.NewGuid():N}.zip");

        try
        {
            status?.Report($"Downloading {assetName}...");

            using var assetResponse = await SendWithRetryAsync(
                () => CreateGitHubRequest(new Uri(downloadUrl), expectsJson: false),
                DownloadRequestTimeout,
                cancellationToken);

            if (IsRateLimited(assetResponse))
            {
                throw new InvalidOperationException(
                    "GitHub API rate limit exceeded while downloading the asset. Set PGNTOOLS_GITHUB_TOKEN or GITHUB_TOKEN to increase the limit.");
            }

            assetResponse.EnsureSuccessStatusCode();

            await using var assetStream = await assetResponse.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            await assetStream.CopyToAsync(fileStream, cancellationToken);
            await fileStream.FlushAsync(cancellationToken);
            fileStream.Flush(true);

            status?.Report("Extracting Stockfish...");
            ZipFile.ExtractToDirectory(tempZipPath, installDirectory, overwriteFiles: true);

            status?.Report("Locating engine executable...");
            var exePath = FindExecutable(installDirectory, variant);
            if (exePath == null)
            {
                throw new FileNotFoundException("Stockfish executable was not found after extraction.", installDirectory);
            }

            status?.Report($"Stockfish ready: {Path.GetFileName(exePath)}");

            return new StockfishDownloadResult(tag, variant.ToString(), installDirectory, exePath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }
            }
            catch
            {
            }
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        // GitHub API requires a User-Agent header.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        return client;
    }

    private static AssetInfo? SelectAsset(JsonElement assets, StockfishVariant preferredVariant)
    {
        var assetsList = assets.EnumerateArray()
            .Select(a => new
            {
                Name = a.GetProperty("name").GetString(),
                ApiUrl = a.TryGetProperty("url", out var apiUrl) ? apiUrl.GetString() : null,
                BrowserUrl = a.TryGetProperty("browser_download_url", out var browserUrl) ? browserUrl.GetString() : null
            })
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) &&
                        (!string.IsNullOrWhiteSpace(a.ApiUrl) || !string.IsNullOrWhiteSpace(a.BrowserUrl)))
            .Select(a => new AssetInfo(a.Name!, a.ApiUrl ?? string.Empty, a.BrowserUrl ?? string.Empty))
            .ToList();

        if (assetsList.Count == 0)
        {
            return null;
        }

        var preferredNames = GetVariantAssetNamesInPriority(preferredVariant);
        foreach (var name in preferredNames)
        {
            var match = assetsList.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
        }

        // Final fallback: any stockfish Windows x64 zip.
        var fallback = assetsList.FirstOrDefault(a =>
            a.Name!.StartsWith("stockfish-windows-x86-64", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        return fallback;
    }

    private static IReadOnlyList<string> GetVariantAssetNamesInPriority(StockfishVariant variant)
    {
        // Order: preferred variant first, then progressively more general options.
        var variants = variant switch
        {
            StockfishVariant.Vnni512 => new[]
            {
                "stockfish-windows-x86-64-vnni512.zip",
                "stockfish-windows-x86-64-vnni256.zip",
                "stockfish-windows-x86-64-avx512.zip",
                "stockfish-windows-x86-64-avx2.zip",
                "stockfish-windows-x86-64-bmi2.zip",
                "stockfish-windows-x86-64-sse41-popcnt.zip",
                "stockfish-windows-x86-64.zip"
            },
            StockfishVariant.Vnni256 => new[]
            {
                "stockfish-windows-x86-64-vnni256.zip",
                "stockfish-windows-x86-64-avx512.zip",
                "stockfish-windows-x86-64-avx2.zip",
                "stockfish-windows-x86-64-bmi2.zip",
                "stockfish-windows-x86-64-sse41-popcnt.zip",
                "stockfish-windows-x86-64.zip"
            },
            StockfishVariant.Avx512 => new[]
            {
                "stockfish-windows-x86-64-avx512.zip",
                "stockfish-windows-x86-64-avx2.zip",
                "stockfish-windows-x86-64-bmi2.zip",
                "stockfish-windows-x86-64-sse41-popcnt.zip",
                "stockfish-windows-x86-64.zip"
            },
            StockfishVariant.Avx2 => new[]
            {
                "stockfish-windows-x86-64-avx2.zip",
                "stockfish-windows-x86-64-bmi2.zip",
                "stockfish-windows-x86-64-sse41-popcnt.zip",
                "stockfish-windows-x86-64.zip"
            },
            StockfishVariant.Bmi2 => new[]
            {
                "stockfish-windows-x86-64-bmi2.zip",
                "stockfish-windows-x86-64-sse41-popcnt.zip",
                "stockfish-windows-x86-64.zip"
            },
            StockfishVariant.Sse41Popcnt => new[]
            {
                "stockfish-windows-x86-64-sse41-popcnt.zip",
                "stockfish-windows-x86-64.zip"
            },
            _ => new[]
            {
                "stockfish-windows-x86-64.zip",
                "stockfish-windows-x86-64-sse41-popcnt.zip"
            }
        };

        return variants;
    }

    private static string GetInstallDirectory(string tag, StockfishVariant variant)
    {
        var variantSegment = variant.ToString().ToLowerInvariant();

        var assetsPreferred = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "stockfish",
            tag,
            variantSegment);

        if (CanWriteToDirectory(assetsPreferred))
        {
            return assetsPreferred;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PgnTools",
            "Stockfish",
            tag,
            variantSegment);
    }

    private static bool CanWriteToDirectory(string directoryPath)
    {
        try
        {
            Directory.CreateDirectory(directoryPath);
            var probePath = Path.Combine(directoryPath, $".write_probe_{Guid.NewGuid():N}.tmp");
            using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindExecutable(string installDirectory, StockfishVariant variant)
    {
        var exes = Directory.EnumerateFiles(installDirectory, "*.exe", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p).Contains("stockfish", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exes.Count == 0)
        {
            return null;
        }

        var variantToken = variant.ToString().ToLowerInvariant();

        var preferred = exes
            .Where(p => Path.GetFileName(p).Contains(variantToken, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Length)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred;
        }

        return exes
            .OrderBy(p => p.Length)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static bool IsAvx512Supported()
    {
#if NET8_0_OR_GREATER
        return Avx512F.IsSupported;
#else
        return false;
#endif
    }

    private static bool IsVnni256Supported()
    {
#if NET8_0_OR_GREATER
        return AvxVnni.IsSupported;
#else
        return false;
#endif
    }

    private static bool IsVnni512Supported()
    {
#if NET8_0_OR_GREATER
        // There is no explicit vnni512 intrinsic in all TFMs; treat AVX-512 VNNI
        // as equivalent to AVX-512 support when available.
        return IsAvx512Supported() && IsVnni256Supported();
#else
        return false;
#endif
    }

    private static HttpRequestMessage CreateGitHubRequest(Uri uri, bool expectsJson)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
            Version = HttpVersion.Version20
        };

        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            expectsJson ? "application/vnd.github+json" : "application/octet-stream"));
        request.Headers.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        var token = GetGitHubToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static string? GetGitHubToken()
    {
        var token = Environment.GetEnvironmentVariable("PGNTOOLS_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token;
        }

        token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token;
        }

        token = Environment.GetEnvironmentVariable("GH_TOKEN");
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var lastException = (Exception?)null;

        for (var attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(timeout);

            using var request = requestFactory();
            try
            {
                var response = await HttpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        attemptCts.Token)
                    .ConfigureAwait(false);

                if (!IsTransientStatusCode(response.StatusCode) || attempt == MaxRetryAttempts)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < MaxRetryAttempts)
            {
                lastException = new TimeoutException("Request timed out.");
            }
            catch (HttpRequestException ex) when (attempt < MaxRetryAttempts)
            {
                lastException = ex;
            }

            var delay = GetRetryDelay(attempt);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Failed to reach GitHub after multiple attempts.", lastException);
    }

    private static TimeSpan GetRetryDelay(int attempt)
    {
        if (attempt <= 0 || attempt > RetryDelays.Length)
        {
            return TimeSpan.FromSeconds(2);
        }

        return RetryDelays[attempt - 1];
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.RequestTimeout ||
               statusCode == HttpStatusCode.TooManyRequests ||
               statusCode == HttpStatusCode.InternalServerError ||
               statusCode == HttpStatusCode.BadGateway ||
               statusCode == HttpStatusCode.ServiceUnavailable ||
               statusCode == HttpStatusCode.GatewayTimeout;
    }

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return false;
        }

        if (!response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining))
        {
            return false;
        }

        foreach (var value in remaining)
        {
            if (string.Equals(value, "0", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record AssetInfo(string Name, string ApiUrl, string BrowserUrl);
}
