using System.Globalization;
using System.Text.Json;

namespace PgnTools.Services;

public interface IAppSettingsService
{
    T GetValue<T>(string key, T defaultValue);
    void SetValue<T>(string key, T value);
    void Remove(string key);
}

public sealed class AppSettingsService : IAppSettingsService
{
    private readonly string _settingsPath;
    private readonly object _lock = new();
    private Dictionary<string, JsonElement>? _cache;

    public AppSettingsService()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PgnTools");
        Directory.CreateDirectory(baseDir);
        _settingsPath = Path.Combine(baseDir, "settings.json");
    }

    public T GetValue<T>(string key, T defaultValue)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return defaultValue;
        }

        lock (_lock)
        {
            var store = GetStore();
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
    }

    public void SetValue<T>(string key, T value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_lock)
        {
            var store = GetStore();
            if (value is null)
            {
                if (store.Remove(key))
                {
                    SaveStore(store);
                }
                return;
            }

            store[key] = JsonSerializer.SerializeToElement(value);
            SaveStore(store);
        }
    }

    public void Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        lock (_lock)
        {
            var store = GetStore();
            if (store.Remove(key))
            {
                SaveStore(store);
            }
        }
    }

    private Dictionary<string, JsonElement> GetStore()
    {
        if (_cache != null)
        {
            return _cache;
        }

        if (!File.Exists(_settingsPath))
        {
            _cache = new Dictionary<string, JsonElement>();
            return _cache;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            _cache = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
                     ?? new Dictionary<string, JsonElement>();
        }
        catch
        {
            _cache = new Dictionary<string, JsonElement>();
        }

        return _cache;
    }

    private void SaveStore(Dictionary<string, JsonElement> store)
    {
        var json = JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }
}
