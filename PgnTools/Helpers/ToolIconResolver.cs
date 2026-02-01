using System.Collections.Concurrent;
using System.Text;

namespace PgnTools.Helpers;

/// <summary>
/// Resolves optional per-tool icon images from Assets with a filename convention.
/// </summary>
public static class ToolIconResolver
{
    private static readonly string[] Extensions = [".png", ".ico", ".jpg", ".jpeg"];
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Uri? ResolveIconUri(string toolKey, string toolName)
    {
        var relativePath = ResolveIconRelativePath(toolKey, toolName);
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        return new Uri(ToAbsolutePath(relativePath), UriKind.Absolute);
    }

    public static string? ResolveIconRelativePath(string toolKey, string toolName)
    {
        var cacheKey = $"{toolKey}|{toolName}";
        var cached = Cache.GetOrAdd(cacheKey, _ => ResolveIconRelativePathCore(toolKey, toolName) ?? string.Empty);
        return string.IsNullOrWhiteSpace(cached) ? null : cached;
    }

    private static string? ResolveIconRelativePathCore(string toolKey, string toolName)
    {
        var tokens = BuildCandidateTokens(toolKey, toolName);
        foreach (var token in tokens)
        {
            foreach (var extension in Extensions)
            {
                var fileName = $"{token}{extension}";
                foreach (var folder in GetCandidateFolders())
                {
                    var relativePath = $"{folder}/{fileName}";
                    if (File.Exists(ToAbsolutePath(relativePath)))
                    {
                        return relativePath;
                    }
                }

                foreach (var prefixed in GetPrefixedFileNames(token, extension))
                {
                    foreach (var folder in GetCandidateFolders())
                    {
                        var relativePath = $"{folder}/{prefixed}";
                        if (File.Exists(ToAbsolutePath(relativePath)))
                        {
                            return relativePath;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCandidateFolders()
    {
        yield return "Assets/Icons";
        yield return "Assets";
    }

    private static IEnumerable<string> GetPrefixedFileNames(string token, string extension)
    {
        yield return $"icon-{token}{extension}";
        yield return $"{token}-icon{extension}";
    }

    private static HashSet<string> BuildCandidateTokens(string toolKey, string toolName)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            toolKey,
            toolName,
            SanitizeToken(toolKey),
            SanitizeToken(toolName),
            toolKey.ToLowerInvariant(),
            SanitizeToken(toolName).ToLowerInvariant()
        };

        tokens.RemoveWhere(string.IsNullOrWhiteSpace);
        return tokens;
    }

    private static string SanitizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static string ToAbsolutePath(string relativePath)
    {
        var localPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(AppContext.BaseDirectory, localPath);
    }
}
