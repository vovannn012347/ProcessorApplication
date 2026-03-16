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
        var user = await _userHelper.FindByIdAsync(userId);
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

        var user = await _userHelper.FindByIdAsync(userId);

        // Note: In a real scenario, the 'userKey' would be provided via the Export Request context
        // This follows your provided logic for identity.json generation

        var filePath = Path.Combine(destinationFolder, "identity.json");
        // ... (JSON Serialization logic from your attached file) ...

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