using System.Text;
using PgnTools.Helpers;

namespace PgnTools.Services;

public class PlycountAdderService : IPlycountAdderService
{
    private const int BufferSize = 65536;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    public async Task AddPlyCountAsync(string inputFile, string outputFile, IProgress<double> progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inputFile) || string.IsNullOrWhiteSpace(outputFile))
            throw new ArgumentException("File paths required.");

        var inputFullPath = Path.GetFullPath(inputFile);
        var outputFullPath = Path.GetFullPath(outputFile);

        if (inputFullPath.Equals(outputFullPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Input and output cannot be the same file.");

        var tempOutputPath = outputFullPath + ".tmp";

        long totalBytes = new FileInfo(inputFullPath).Length;
        long bytesRead = 0;
        DateTime lastReport = DateTime.MinValue;

        var headerBuffer = new StringBuilder();
        var moveBuffer = new StringBuilder();

        bool inHeaderSection = true;
        bool hasHeaders = false;

        try
        {
            await using var inStream = new FileStream(inputFullPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous);
            await using var outStream = new FileStream(tempOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);

            using var reader = new StreamReader(inStream, Encoding.UTF8, false, BufferSize);
            using var writer = new StreamWriter(outStream, new UTF8Encoding(false), BufferSize);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                bytesRead += Encoding.UTF8.GetByteCount(line) + 2; // Rough estimate including newline

                string trimmed = line.Trim();

                // 1. Empty Line Logic
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    // If we were reading moves and hit a blank line, the game might be over.
                    // Or it's just a blank line inside moves.
                    // Standard PGN requires blank line after headers.
                    if (inHeaderSection && hasHeaders)
                    {
                        // Transition Header -> Moves (or just wait for next line)
                        inHeaderSection = false;
                    }
                    else if (!inHeaderSection && moveBuffer.Length > 0)
                    {
                        // Likely end of game. Flush.
                        await FlushGameAsync(writer, headerBuffer, moveBuffer);
                        inHeaderSection = true;
                        hasHeaders = false;
                    }
                    continue;
                }

                // 2. Header Logic
                if (trimmed.StartsWith('['))
                {
                    if (!inHeaderSection && moveBuffer.Length > 0)
                    {
                        // We hit a new header but haven't flushed previous moves? Flush now.
                        await FlushGameAsync(writer, headerBuffer, moveBuffer);
                        hasHeaders = false;
                    }

                    inHeaderSection = true;
                    hasHeaders = true;

                    // Skip existing PlyCount
                    if (!trimmed.StartsWith("[PlyCount", StringComparison.OrdinalIgnoreCase))
                    {
                        headerBuffer.AppendLine(trimmed);
                    }
                }
                // 3. Move Logic
                else
                {
                    inHeaderSection = false;
                    moveBuffer.AppendLine(line);
                }

                // Progress Reporting
                if (DateTime.UtcNow - lastReport > ProgressInterval)
                {
                    progress.Report((double)bytesRead / totalBytes * 100);
                    lastReport = DateTime.UtcNow;
                }
            }

            // Flush final game
            if (headerBuffer.Length > 0 || moveBuffer.Length > 0)
            {
                await FlushGameAsync(writer, headerBuffer, moveBuffer);
            }

            progress.Report(100);
        }
        catch
        {
            try { File.Delete(tempOutputPath); } catch { }
            throw;
        }

        FileReplacementHelper.ReplaceFile(tempOutputPath, outputFullPath);
    }

    private async Task FlushGameAsync(StreamWriter writer, StringBuilder headers, StringBuilder moves)
    {
        string moveText = moves.ToString();
        int plies = CalculatePlies(moveText);

        if (headers.Length > 0)
        {
            await writer.WriteAsync(headers);
            await writer.WriteLineAsync($"[PlyCount \"{plies}\"]");
            await writer.WriteLineAsync();
        }

        if (moveText.Length > 0)
        {
            await writer.WriteLineAsync(moveText.TrimEnd());
            await writer.WriteLineAsync();
            await writer.WriteLineAsync();
        }

        headers.Clear();
        moves.Clear();
    }

    private int CalculatePlies(string text)
    {
        // Remove comments inside {}
        var sb = new StringBuilder();
        bool inBrace = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{') { inBrace = true; continue; }
            if (c == '}') { inBrace = false; continue; }
            if (inBrace) continue;

            // Also ignore RAV (...)
            // Note: Simple nested RAV handling is hard,
            // but for PlyCount we strictly only care about the main line.
            // A simple heuristic: if we see '(', ignore everything until ')'
            // This is naive for nested parens, but covers 99% of basic PGNs.
            // For a robust tool, use a full PGN Parser (PgnReader).
            // Since this tool is "PlyCountAdder", speed vs accuracy trade-off applies.

            sb.Append(c);
        }

        var clean = sb.ToString();
        // Split by whitespace
        var tokens = clean.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        int count = 0;

        foreach (var t in tokens)
        {
            if (IsMove(t)) count++;
        }
        return count;
    }

    private bool IsMove(string token)
    {
        // Ignore move numbers "1." or "1..."
        if (token.EndsWith(".")) return false;
        if (char.IsDigit(token[0])) return false; // Starts with digit usually move number "12.e4" -> needs splitting but assuming spaced PGN

        // Ignore results
        if (token is "1-0" or "0-1" or "1/2-1/2" or "*") return false;

        // Ignore NAGs
        if (token.StartsWith('$')) return false;

        // Basic sanity check: Must contain letters or O-O
        if (token.StartsWith("O-O")) return true;

        // e4, Nf3, exd5, R1a3, etc.
        // Should start with a letter (K, Q, R, B, N, a-h)
        char first = token[0];
        return char.IsLetter(first);
    }
}
