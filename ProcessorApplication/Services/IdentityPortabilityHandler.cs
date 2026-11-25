using System.Text.Json;

using Common.Infrastructure;
using Common.Interfaces;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

using ProcessorApplication.Database;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Models.User;
using ProcessorApplication.Services.User;

namespace ProcessorApplication.Services;

//public class IdentityPortabilityHandler : IModuleDataPortability
//{
//    private readonly AppDbContext _dbContext;
//    private readonly ProcessorApplicationUserManager _userHelper;
//    //private readonly Logger<IdentityPortabilityHandler> _logger;

//    public IdentityPortabilityHandler(
//        AppDbContext dbContext,
//        //Logger<IdentityPortabilityHandler> logger,
//        ProcessorApplicationUserManager userHelper)
//    {
//        _dbContext = dbContext;
//        _userHelper = userHelper;
//        //_logger = logger;
//    }

//    public string DataKey => "Identity";

//    public async Task<string> ExportUserDataAsync(string userName, string userKey, string destinationFolder)
//    {
//        var user = await _userHelper.FindByNameAsync(userName);
//        _dbContext.Entry(user).State = EntityState.Detached;

//        if (_userHelper.DecryptUserDataDirect(user, userKey))
//        {
//            // Prepare the DTO
//            var exportModel = new ProfileExportModel
//            {
//                UserName = user.UserName,
//                Name = user.Name,
//                Surname = user.Surname,
//                Email = user.Email,
//                DisplayNickname = user.DisplayNickname,
//                PersonalHashKey = user.PersonalHashKeyLockedByPassword,
//                UserIdLockedByPHSK = user.UserIdLockedByPHSK
//            };

//            var options = new JsonSerializerOptions { WriteIndented = true };
//            var json = JsonSerializer.Serialize(exportModel, options);

//            var filePath = Path.Combine(destinationFolder, user.UserName);
//            if (!Directory.Exists(filePath)) Directory.CreateDirectory(filePath);
//            filePath = Path.Combine(filePath, "identity.json");

//            await File.WriteAllTextAsync(filePath, json);
//            return filePath;
//        }

//        return string.Empty;
//    }


//}

public class IdentityPortabilityHandler : IPortabilityHandler
{
    private readonly ProcessorApplicationUserManager _userHelper;
    private readonly AppDbContext _dbContext;

    // The constructor was missing, so both fields stayed null and every export failed with a NullReferenceException
    public IdentityPortabilityHandler(ProcessorApplicationUserManager userHelper, AppDbContext dbContext)
    {
        _userHelper = userHelper;
        _dbContext = dbContext;
    }

    /// <summary>The caller may pass the user name (Profile download) or the user id.</summary>
    private async Task<ApplicationUser?> FindUserAsync(string userNameOrId) =>
        await _userHelper.FindByNameAsync(userNameOrId) ?? await _userHelper.FindByIdAsync(userNameOrId);

    public string ModuleIdentifier => "Identity";

    public async Task<ModuleExportSummary> GetExportSummaryAsync(string userId)
    {
        return new ModuleExportSummary
        {
            DisplayName = "User Identity & Profile",
            Description = "Exports personal profile data, encrypted keys, and account metadata.",
            TotalItemCount = 1
        };
    }

    public async Task<PaginatedList<ExportableItem>> GetExportableItemsAsync(string userId, int pageIndex, int pageSize)
    {
        var user = await FindUserAsync(userId);
        if (user == null) return new PaginatedList<ExportableItem>(new List<ExportableItem>(), 0, pageIndex, pageSize);
        var items = new List<ExportableItem> {
            new ExportableItem {
                Id = "profile_main",
                DisplayName = $"{user.UserName} Full Profile",
                Metadata = "Includes PHSK and personal hash keys"
            }
        };
        return new PaginatedList<ExportableItem>(items, 1, pageIndex, pageSize);
    }

    public async Task<List<string>> ProcessExportAsync(string userId, List<string> selectedItemIds, string userKey, string destinationFolder)
    {
        if (!selectedItemIds.Contains("profile_main")) return new List<string>();

        var user = await FindUserAsync(userId);
        if (user == null) return new List<string>();

        // Work on a detached copy: decrypting must not leave plain values tracked for the next SaveChanges
        _dbContext.Entry(user).State = EntityState.Detached;
        if (!_userHelper.DecryptUserDataDirect(user, userKey)) return new List<string>();

        // The file identifier: profile fields plus the personal key locked by the user's password and the
        // user name locked by that key (LoginFromFile / RegisterFromFile check both)
        var exportModel = new ProfileExportModel
        {
            UserName = user.UserName,
            Name = user.Name,
            Surname = user.Surname,
            Email = user.Email,
            DisplayNickname = user.DisplayNickname,
            PersonalHashKey = user.PersonalHashKeyLockedByPassword,
            UserIdLockedByPHSK = user.UserIdLockedByPHSK
        };

        Directory.CreateDirectory(destinationFolder);
        var filePath = Path.Combine(destinationFolder, "identity.json");
        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(exportModel, new JsonSerializerOptions { WriteIndented = true }));

        return new List<string> { "identity.json" };
    }

    public async Task<bool> ProcessImportAsync(string userId, string sourceFolder)
    {
        // Restore auxiliary data from identity.json if needed
        return true;
    }
    public async Task<bool> ImportUserDataAsync(string userId, string userKey, string destinationFolder)
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

        return true;
        //return string.Empty;
    }
}