using System.Text;

namespace PgnTools.Services;

public interface IPgnJoinerService
{
    Task JoinFilesAsync(
        IEnumerable<string> sourceFiles,
        string destinationFile,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}

public class PgnJoinerService : IPgnJoinerService
{
    private const int BufferSize = 65536;

    public async Task JoinFilesAsync(
        IEnumerable<string> sourceFiles,
        string destinationFile,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (sourceFiles is null)
        {
            throw new ArgumentNullException(nameof(sourceFiles));
        }

        var files = sourceFiles
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            throw new ArgumentException("At least one source file is required.", nameof(sourceFiles));
        }

        if (string.IsNullOrWhiteSpace(destinationFile))
        {
            throw new ArgumentException("Destination file path is required.", nameof(destinationFile));
        }

        var outputFullPath = Path.GetFullPath(destinationFile);
        var tempOutputPath = FileReplacementHelper.CreateTempFilePath(outputFullPath);

        if (files.Any(path => string.Equals(path, outputFullPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Output file must be different from input files.");
        }

        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                throw new FileNotFoundException("Input PGN file not found.", file);
            }
        }

        if (Path.GetDirectoryName(outputFullPath) is { } directory)
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            await using (var outputStream = new FileStream(
                tempOutputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous))
            using (var writer = new StreamWriter(outputStream, new UTF8Encoding(false), BufferSize))
            {
                progress?.Report(0);
                var wroteAny = false;

                for (var index = 0; index < files.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();

                    var inputPath = files[index];
                    await using var inputStream = new FileStream(
                        inputPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        BufferSize,
                        FileOptions.SequentialScan | FileOptions.Asynchronous);
                    using var reader = new StreamReader(
                        inputStream,
                        Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: true,
                        bufferSize: BufferSize,
                        leaveOpen: true);

                    var started = false;
                    var pendingBlank = 0;

                    while (await reader.ReadLineAsync(ct) is { } line)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (string.IsNullOrWhiteSpace(line))
                        {
                            if (started)
                            {
                                pendingBlank++;
                            }
                            continue;
                        }

                        if (!started)
                        {
                            if (wroteAny)
                            {
                                await writer.WriteLineAsync();
                                await writer.WriteLineAsync();
                            }
                            wroteAny = true;
                            started = true;
                        }

                        if (pendingBlank > 0)
                        {
                            for (var i = 0; i < pendingBlank; i++)
                            {
                                await writer.WriteLineAsync();
                            }
                            pendingBlank = 0;
                        }

                        await writer.WriteLineAsync(line);
                    }

                    progress?.Report((index + 1) / (double)files.Count * 100.0);
                }

                if (wroteAny)
                {
                    await writer.WriteLineAsync();
                }

                await writer.FlushAsync();
            }

            FileReplacementHelper.ReplaceFile(tempOutputPath, outputFullPath);
            progress?.Report(100);
        }
        catch
        {
            if (File.Exists(tempOutputPath))
            {
                try
                {
                    File.Delete(tempOutputPath);
                }
                catch
                {
                }
            }
            throw;
        }
    }
}
