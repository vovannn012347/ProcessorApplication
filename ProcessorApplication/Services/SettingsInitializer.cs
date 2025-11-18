using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime;
using System.Text.Json;

using Common.Attributes;
using Common.Code;
using Common.Interfaces;
using Common.Models;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Internal;

using ProcessorApplication.Sqlite;
using ProcessorApplication.Sqlite.Models;

namespace ProcessorApplication.Services;

public class SettingsInitializer : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<SettingsInitializer> _log;

    public SettingsInitializer(IServiceProvider sp, ILogger<SettingsInitializer> log)
    {
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var scope = _sp.CreateScope();
        var settingService = scope.ServiceProvider.GetRequiredService<ISettingService>();

        await settingService.SeedDefaultsIfEmptyAsync(stoppingToken);
    }
}

//public class SettingsInitializer : IHostedService
//{
//    private readonly IServiceScopeFactory _scopeFactory;

//    public SettingsInitializer(IServiceScopeFactory serviceScopeFactory)
//    {
//        _scopeFactory = serviceScopeFactory;
//    }

//    public async Task StartAsync(CancellationToken cancellationToken)
//    {
//        using var scope = _scopeFactory.CreateScope();
//        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
//        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

//        var settingTypes = Assembly.GetExecutingAssembly()
//            .GetTypes()
//            .Where(t => typeof(ISettings).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

//        //get unflatterned settings from database

//        using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
//        try
//        {
//            foreach (var type in settingTypes)
//            {
//                var prefix = type.GetCustomAttribute<SettingKeyAttribute>()?.Key ?? type.Name.Replace("Settings", "") + ":";
//                var instance = Activator.CreateInstance(type) as ISettings;
//                var configInstance = configuration.GetSection(type.Name).Get(type) as ISettings;

//                var dbInstSettings = await dbContext.Settings.AsNoTracking()
//                    .Where(s => s.Key.StartsWith(prefix))
//                    .ToDictionaryAsync(
//                        s => s.Key.Substring(prefix.Length),
//                        s => s.IsSensitive ? Util.Decrypt(s.Value ?? string.Empty) : s.Value ?? string.Empty
//                    );

//                //this overrides
//                configInstance = configInstance.FromDbSettings(dbInstSettings);

//                foreach (var kvp in configInstance.GetSettingForDb())
//                {
//                    var key = $"{prefix}{kvp.Key}";
//                    var setting = await dbContext.Settings.FirstOrDefaultAsync(s => s.Key == key);
//                    bool isSensitive = configInstance.IsSettingSensitive(kvp.Key);
//                    if (setting == null)
//                    {
//                        setting = new Setting
//                        {
//                            Key = key,
//                            Value = isSensitive ? Util.Encrypt(kvp.Value) : kvp.Value,
//                            IsSensitive = isSensitive
//                        };
//                        dbContext.Settings.Add(setting);
//                    }
//                    else
//                    {
//                        setting.Value = isSensitive ? Util.Encrypt(kvp.Value) : kvp.Value;
//                        setting.IsSensitive = isSensitive;
//                    }
//                }
//                await dbContext.SaveChangesAsync(cancellationToken);
//            }

//            await transaction.CommitAsync(cancellationToken);
//        }
//        catch
//        {
//            await transaction.RollbackAsync(cancellationToken);
//            throw;
//        }
//    }

//    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
//}
