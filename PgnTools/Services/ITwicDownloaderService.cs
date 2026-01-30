namespace PgnTools.Services;

/// <summary>
/// Interface for downloading TWIC issues into a single PGN file.
/// </summary>
public interface ITwicDownloaderService
{
    Task DownloadIssuesAsync(int start, int end, string outputFile, IProgress<string> status, CancellationToken ct);
}
