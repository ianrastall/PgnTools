namespace Windows.Storage;

public sealed class StorageFile(string path)
{
    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(Path);

    public Task<Stream> OpenReadAsync()
    {
        Stream stream = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }
}

public sealed class StorageFolder(string path)
{
    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
}
