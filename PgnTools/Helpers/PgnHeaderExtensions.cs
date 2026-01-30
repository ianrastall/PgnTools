namespace PgnTools.Helpers;

public static class PgnHeaderExtensions
{
    public static bool TryGetHeaderValue(
        this IReadOnlyDictionary<string, string> headers,
        string key,
        out string value)
    {
        if (headers == null)
        {
            throw new ArgumentNullException(nameof(headers));
        }

        if (string.IsNullOrEmpty(key))
        {
            value = string.Empty;
            return false;
        }

        if (headers.TryGetValue(key, out var found))
        {
            value = found ?? string.Empty;
            return true;
        }

        foreach (var header in headers)
        {
            if (string.Equals(header.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = header.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    public static string? GetHeaderValueOrDefault(
        this IReadOnlyDictionary<string, string> headers,
        string key)
    {
        return headers.TryGetHeaderValue(key, out var value) ? value : null;
    }

    public static string GetHeaderValueOrDefault(
        this IReadOnlyDictionary<string, string> headers,
        string key,
        string defaultValue)
    {
        return headers.TryGetHeaderValue(key, out var value) ? value : defaultValue;
    }

    public static bool TryGetHeaderValue(
        this IReadOnlyList<KeyValuePair<string, string>> headers,
        string key,
        out string value)
    {
        if (headers == null)
        {
            throw new ArgumentNullException(nameof(headers));
        }

        if (string.IsNullOrEmpty(key))
        {
            value = string.Empty;
            return false;
        }

        for (var i = headers.Count - 1; i >= 0; i--)
        {
            var header = headers[i];
            if (string.Equals(header.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = header.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    public static string? GetHeaderValueOrDefault(
        this IReadOnlyList<KeyValuePair<string, string>> headers,
        string key)
    {
        return headers.TryGetHeaderValue(key, out var value) ? value : null;
    }

    public static string GetHeaderValueOrDefault(
        this IReadOnlyList<KeyValuePair<string, string>> headers,
        string key,
        string defaultValue)
    {
        return headers.TryGetHeaderValue(key, out var value) ? value : defaultValue;
    }
}
