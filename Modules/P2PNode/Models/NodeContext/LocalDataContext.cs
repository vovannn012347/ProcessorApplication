using System.Collections.Concurrent;
using System.Runtime.InteropServices;

using Common.Interfaces.EventBus;

namespace Common.Models.NodeContext;
/*
 * is generally here to store different datetimes
 **/
public class LocalDataContext : ConcurrentDictionary<string, object>, ICacheInstance
{
    public const string MyHashKey = "MyHashId";
    public const string MyFriendlyNameKey = "MyFriendlyName";
    public const string LoadKey = "MyFriendlyName";

    private T GetOrAddValue<T>(string key, T defaultValue)
    {
        if (TryGetValue(key, out var value))
        {
            if (typeof(T).IsEnum)
            {
                if (value is string strValue &&
                    Enum.TryParse(typeof(T), strValue, ignoreCase: true, out var enumValue))
                    return (T)enumValue;
            }

            if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(typeof(T)))
            {
                try { return (T)Convert.ChangeType(value, typeof(T)); }
                catch { /* ignore */ }
            }

            if (value is T typedValue)
                return typedValue;
        }

        // not found → add default
        this[key] = defaultValue!;
        return defaultValue;
    }

    public T Get<T>(string key, T defaultValue, string prefix = "")
        => !string.IsNullOrEmpty(prefix) ?
            GetOrAddValue($"{prefix}:{key}", defaultValue) :
            GetOrAddValue(key, defaultValue);

    public void Set<T>(string key, T value, string prefix = "")
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));

        string actualKey = string.IsNullOrEmpty(prefix) ? key : $"{prefix}:{key}";

        if (value is Enum en)
            this[actualKey] = en.ToString();
        else
            this[actualKey] = value;
    }

    public void LoadSettings<T>(T settings, string prefix) where T : class
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        if (string.IsNullOrEmpty(prefix)) throw new ArgumentException("Prefix cannot be empty", nameof(prefix));

        foreach (var prop in typeof(T).GetProperties())
        {
            var value = prop.GetValue(settings);
            if (value == null) continue;

            string key = $"{prefix}:{prop.Name}";
            if (value is string str)
                Set<string>(key, str);
            else if (value is int i)
                Set<int>(key, i);
            else if (value is double d)
                Set<double>(key, d);
            else if (value is float f)
                Set<float>(key, f);
            else if (value is Enum en)
                    Set<string>(key, en.ToString());
        }
    }
}
