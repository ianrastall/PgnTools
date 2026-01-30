namespace PgnTools.Services;

/// <summary>
/// Interface for adding PlyCount tags to PGN files.
/// </summary>
public interface IPlycountAdderService
{
    Task AddPlyCountAsync(string inputFile, string outputFile, IProgress<double> progress, CancellationToken ct);
}
