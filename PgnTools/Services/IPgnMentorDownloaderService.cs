namespace PgnTools.Services;

/// <summary>
/// Interface for downloading and combining PGN Mentor files.
/// </summary>
public interface IPgnMentorDownloaderService
{
    Task DownloadAndCombineAsync(string outputFile, IProgress<string> status, CancellationToken ct);
}
