using System.Text.Json;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Database;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Models.User;
using ProcessorApplication.Services.User;

namespace ProcessorApplication.Services;

public class IdentityPortabilityHandler : IModuleDataPortability
{
    private readonly AppDbContext _dbContext;
    private readonly ProcessorApplicationUserManager _userHelper;
    //private readonly Logger<IdentityPortabilityHandler> _logger;

    public IdentityPortabilityHandler(
        AppDbContext dbContext,
        //Logger<IdentityPortabilityHandler> logger,
        ProcessorApplicationUserManager userHelper)
    {
        _dbContext = dbContext;
        _userHelper = userHelper;
        //_logger = logger;
    }

    public string DataKey => "Identity";

    public async Task<string> ExportUserDataAsync(string userName, string userKey, string destinationFolder)
    {
        var user = await _userHelper.FindByNameAsync(userName);
        _dbContext.Entry(user).State = EntityState.Detached;

        if (_userHelper.DecryptUserDataDirect(user, userKey))
        {
            // Prepare the DTO
            var exportModel = new ProfileExportModel
            {
                UserName = user.UserName,
                Name = user.Name,
                Surname = user.Surname,
                DisplayNickname = user.DisplayNickname,
                PersonalHashKey = user.PersonalHashKeyLockedByPassword,
                UserIdLockedByPHSK = user.UserIdLockedByPHSK
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(exportModel, options);

            var filePath = Path.Combine(destinationFolder, user.UserName);
            if (!Directory.Exists(filePath)) Directory.CreateDirectory(filePath);
            filePath = Path.Combine(filePath, "identity.json");

            await File.WriteAllTextAsync(filePath, json);
            return filePath;
        }

        return string.Empty;
    }

    public async Task<string> ImportUserDataAsync(string userName, string userKey, string destinationFolder)
    {
        // Identity Import is special.
        // Usually, the User Record is created *before* calling Import on handlers.
        // However, we might use this to restore auxiliary data like Roles or specific settings 
        // if they weren't part of the initial creation.

        //var filePath = Path.Combine(sourceFolder, "identity.json");
        //if (!File.Exists(filePath)) return;

        // In a full restore scenario, we might simply acknowledge that Identity 
        // was already restored by the 'RegisterFromFile' process logic.
        // But if we had extra tables (like UserLogins, Tokens), we would restore them here.

        return string.Empty;
    }
}