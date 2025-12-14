namespace Common.Interfaces;

public interface ISettingService
{
    Task<T> GetAsync<T>(string area, string key, T defaultValue) where T : class, new();
    Task SetAsync<T>(string area, string key, T value);
    Task SeedDefaultsIfEmptyAsync(CancellationToken stoppingToken);
    void SetAutoUpdate(bool autoupdate);
    void ForceUpdateOptionsMonitor();
}