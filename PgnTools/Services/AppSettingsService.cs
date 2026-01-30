using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Storage;

namespace PgnTools.Services;

public interface IAppSettingsService
{
    T GetValue<T>(string key, T defaultValue);
    void SetValue<T>(string key, T value);
    void Remove(string key);
}

/// <summary>
/// Simple settings store backed by ApplicationData.LocalSettings.
/// </summary>
public sealed class AppSettingsService : IAppSettingsService
{
    private readonly ApplicationDataContainer? _localSettings;
    private readonly string? _fallbackPath;
    private readonly object _fallbackLock = new();
    private Dictionary<string, JsonElement>? _fallbackCache;

    public AppSettingsService()
    {
        try
        {
            _localSettings = ApplicationData.Current.LocalSettings;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _localSettings = null;
        }

        if (_localSettings == null)
        {
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PgnTools");
            Directory.CreateDirectory(baseDir);
            _fallbackPath = Path.Combine(baseDir, "settings.json");
        }
    }

    public T GetValue<T>(string key, T defaultValue)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return defaultValue;
        }

        if (_localSettings == null)
        {
            return GetFallbackValue(key, defaultValue);
        }

        if (!_localSettings.Values.TryGetValue(key, out var raw) || raw is null)
        {
            return defaultValue;
        }

        if (raw is T typed)
        {
            return typed;
        }

        var targetType = typeof(T);
        var nullableType = Nullable.GetUnderlyingType(targetType);
        if (nullableType != null)
        {
            try
            {
                if (raw is string textValue && nullableType == typeof(DateTimeOffset))
                {
                    if (DateTimeOffset.TryParse(textValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                    {
                        return (T)(object)dto;
                    }
                }

                if (raw.GetType() == nullableType)
                {
                    return (T)raw;
                }

                var converted = Convert.ChangeType(raw, nullableType, CultureInfo.InvariantCulture);
                if (converted != null)
                {
                    return (T)converted;
                }
            }
            catch
            {
            }
        }

        if (raw is string text)
        {
            if (typeof(T) == typeof(DateTimeOffset) &&
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            {
                return (T)(object)dto;
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<T>(text);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch
            {
            }
        }

        try
        {
            return (T)Convert.ChangeType(raw, typeof(T), CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    public void SetValue<T>(string key, T value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (value is null)
        {
            if (_localSettings != null)
            {
                _localSettings.Values.Remove(key);
            }
            else
            {
                RemoveFallback(key);
            }
            return;
        }

        if (_localSettings != null)
        {
            if (value is string ||
                value is bool ||
                value is int ||
                value is long ||
                value is double ||
                value is DateTimeOffset)
            {
                _localSettings.Values[key] = value;
                return;
            }

            _localSettings.Values[key] = JsonSerializer.Serialize(value);
            return;
        }

        SetFallbackValue(key, value);
    }

    public void Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (_localSettings != null)
        {
            _localSettings.Values.Remove(key);
        }
        else
        {
            RemoveFallback(key);
        }
    }

    private T GetFallbackValue<T>(string key, T defaultValue)
    {
        var store = GetFallbackStore();
        if (!store.TryGetValue(key, out var raw))
        {
            return defaultValue;
        }

        try
        {
            if (typeof(T) == typeof(DateTimeOffset) && raw.ValueKind == JsonValueKind.String)
            {
                var text = raw.GetString();
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                {
                    return (T)(object)dto;
                }
            }

            var parsed = raw.Deserialize<T>();
            if (parsed is not null)
            {
                return parsed;
            }
        }
        catch
        {
        }

        return defaultValue;
    }

    private void SetFallbackValue<T>(string key, T value)
    {
        var store = GetFallbackStore();
        try
        {
            store[key] = JsonSerializer.SerializeToElement(value);
            SaveFallbackStore(store);
        }
        catch
        {
        }
    }

    private void RemoveFallback(string key)
    {
        var store = GetFallbackStore();
        if (store.Remove(key))
        {
            SaveFallbackStore(store);
        }
    }

    private Dictionary<string, JsonElement> GetFallbackStore()
    {
        lock (_fallbackLock)
        {
            if (_fallbackCache != null)
            {
                return _fallbackCache;
            }

            if (string.IsNullOrWhiteSpace(_fallbackPath) || !File.Exists(_fallbackPath))
            {
                _fallbackCache = new Dictionary<string, JsonElement>();
                return _fallbackCache;
            }

            try
            {
                var json = File.ReadAllText(_fallbackPath);
                _fallbackCache = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                                 ?? new Dictionary<string, JsonElement>();
            }
            catch
            {
                _fallbackCache = new Dictionary<string, JsonElement>();
            }

            return _fallbackCache;
        }
    }

    private void SaveFallbackStore(Dictionary<string, JsonElement> store)
    {
        if (string.IsNullOrWhiteSpace(_fallbackPath))
        {
            return;
        }

        lock (_fallbackLock)
        {
            var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_fallbackPath, json);
        }
    }
}
