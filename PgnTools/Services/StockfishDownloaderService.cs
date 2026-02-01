using System.IO.Compression;
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

        using var response = await HttpClient.GetAsync(LatestReleaseUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);

        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "latest";
        var assets = root.GetProperty("assets");

        var variant = SelectBestVariant();
        var asset = SelectAsset(assets, variant);
        if (asset == null)
        {
            throw new InvalidOperationException("No compatible Stockfish Windows asset was found in the latest release.");
        }

        var assetName = asset.Value.name;
        var downloadUrl = asset.Value.url;

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

            using (var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl))
            {
                request.Headers.Accept.ParseAdd("application/octet-stream");

                using var assetResponse = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                assetResponse.EnsureSuccessStatusCode();

                await using var assetStream = await assetResponse.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
                await assetStream.CopyToAsync(fileStream, cancellationToken);
            }

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
            Timeout = TimeSpan.FromMinutes(5)
        };

        // GitHub API requires a User-Agent header.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PgnTools/1.0 (GitHub; PgnTools)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static (string name, string url)? SelectAsset(JsonElement assets, StockfishVariant preferredVariant)
    {
        var assetsList = assets.EnumerateArray()
            .Select(a => new
            {
                Name = a.GetProperty("name").GetString(),
                Url = a.GetProperty("url").GetString()
            })
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Url))
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
                return (match.Name!, match.Url!);
            }
        }

        // Final fallback: any stockfish Windows x64 zip.
        var fallback = assetsList.FirstOrDefault(a =>
            a.Name!.StartsWith("stockfish-windows-x86-64", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        return fallback == null ? null : (fallback.Name!, fallback.Url!);
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
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PgnTools",
            "Stockfish",
            tag,
            variant.ToString().ToLowerInvariant());

        return baseDir;
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
}
