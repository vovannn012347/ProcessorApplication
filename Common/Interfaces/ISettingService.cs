namespace Common.Interfaces;
//public interface ISettingService
//{
//    Task<T> GetSettingsAsync<T>() where T : class, ISettings;
//    Task SaveSettingsAsync<T>(T settings) where T : class, ISettings;
//    Task<string> GetSettingAsync(string key, string defaultValue = "", string prefix = "");
//    Task<T> GetSettingAsync<T>(string key, T defaultValue, string prefix = "", Func<string, T>? resultFunction = null);
//    Task SetSettingAsync(string key, string value, bool isSensitive = false, string prefix = "");
//}

public interface ISettingService
{
    Task<T> GetAsync<T>(string area, string key, T defaultValue) where T : class, new();
    Task SetAsync<T>(string area, string key, T value);
    Task SeedDefaultsIfEmptyAsync(CancellationToken stoppingToken);
}