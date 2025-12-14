using System.IO.Compression;
using System.Text.Json;

using Common.Interfaces.Menu;
using Common.Models;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using PortabilityModule.Models;

using ProcessorApplication.Configuration;
using ProcessorApplication.Database;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Models.User;
using ProcessorApplication.Services.User;

namespace PortabilityModule.Services;

public interface IDataPortabilityService
{
    Task<string> GenerateFullExportPackageAsync(ApplicationUser user);
    Task ImportFullPackageAsync(ApplicationUser user, string zipFilePath);
}

public class DataPortabilityService : IDataPortabilityService
{
    private readonly IEnumerable<IUserDataPortabilityHandler> _handlers;
    private readonly ProcessorApplicationUserManager _userHelper;
    private readonly DataPortabilitySettings _settings;

    // This is the template for the offline viewer
    private const string HtmlViewerTemplate = @"
<!DOCTYPE html>
<html>
<head>
    <title>User Data Export</title>
    <style>body { font-family: sans-serif; padding: 2rem; } .module-section { border: 1px solid #ccc; padding: 1rem; margin-bottom: 1rem; border-radius: 8px; } h1 { color: #333; } pre { background: #f4f4f4; padding: 10px; overflow-x: auto; }</style>
</head>
<body>
    <h1>Offline Data Viewer</h1>
    <div id='content'>Loading...</div>
    <script>
        async function load() {
            const container = document.getElementById('content');
            container.innerHTML = '';
            
            // List of modules to try loading (populated dynamically by generator)
            const modules = [__MODULE_LIST__];

            for(const mod of modules) {
                try {
                    const response = await fetch(mod + '.json');
                    if(response.ok) {
                        const data = await response.json();
                        const div = document.createElement('div');
                        div.className = 'module-section';
                        div.innerHTML = `<h2>${mod}</h2><pre>${JSON.stringify(data, null, 2)}</pre>`;
                        container.appendChild(div);
                    }
                } catch(e) { console.error(e); }
            }
        }
        load();
    </script>
</body>
</html>";

    public DataPortabilityService(
        IEnumerable<IUserDataPortabilityHandler> handlers,
        ProcessorApplicationUserManager userHelper,
        IOptionsMonitor<DataPortabilitySettings> settings)
    {
        _handlers = handlers;
        _userHelper = userHelper;
        _settings = settings.CurrentValue; // Or .Get(ModuleId) if fully wired
    }

    public async Task<string> GenerateFullExportPackageAsync(
        ApplicationUser user
        )
    {
        // 1. Prepare Directory
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        string workDir = Path.Combine(_settings.TempStoragePath, $"{user.UserName}_{timestamp}");
        Directory.CreateDirectory(workDir);

        List<string> exportedModules = new List<string>();

        // 2. Export Identity (The "Identifier File")
        var identityExport = new ProfileExportModel
        {
            Identifier = user.UserName,
            Name = user.Name,
            Surname = user.Surname,
            DisplayNickname = user.DisplayNickname,
            HashSignKey = user.PersonalHashKeyLockedByPassword, // Critical: Only works if logged in/decrypted
            UserIdLockedByPHSK = user.UserIdLockedByPHSK
        };

        string identityJson = JsonSerializer.Serialize(identityExport, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(workDir, "identity.json"), identityJson);
        exportedModules.Add("identity");

        // 3. Export Module Data
        foreach (var handler in _handlers)
        {
            try
            {
                // Each handler writes its own file(s) to the work directory
                await handler.ExportUserDataAsync(user.UserName, workDir);
                exportedModules.Add(handler.DataKey);
            }
            catch (Exception ex)
            {
                // Log error but continue export of other modules
                await File.WriteAllTextAsync(Path.Combine(workDir, $"{handler.DataKey}_ERROR.txt"), ex.ToString());
            }
        }

        // 4. Generate HTML Viewer
        var modListJs = string.Join(",", exportedModules.Select(m => $"'{m}'"));
        var htmlContent = HtmlViewerTemplate.Replace("__MODULE_LIST__", modListJs);
        await File.WriteAllTextAsync(Path.Combine(workDir, "index.html"), htmlContent);

        // 5. Zip it up
        string zipPath = $"{workDir}.zip";
        ZipFile.CreateFromDirectory(workDir, zipPath);

        // Cleanup temp folder (keep zip)
        Directory.Delete(workDir, true);

        return zipPath;
    }

    public async Task ImportFullPackageAsync(ApplicationUser user, string zipFilePath)
    {
        string extractPath = Path.Combine(_settings.TempStoragePath, $"import_{Guid.NewGuid()}");
        Directory.CreateDirectory(extractPath);

        try
        {
            ZipFile.ExtractToDirectory(zipFilePath, extractPath);

            //find a root profile model first and then import


            foreach (var handler in _handlers)
            {
                await handler.ImportUserDataAsync(user, extractPath);
            }
        }
        finally
        {
            if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
        }
    }
}