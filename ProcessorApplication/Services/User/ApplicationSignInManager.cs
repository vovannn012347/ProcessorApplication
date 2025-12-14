

using Common.Interfaces.Menu;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

using ProcessorApplication.Database.Models;

namespace ProcessorApplication.Services.User;

public class ApplicationSignInManager : SignInManager<ApplicationUser>
{
    private readonly SecurityHelperUtil _userHelperService;

    public ApplicationSignInManager(
        UserManager<ApplicationUser> userManager, 
        IHttpContextAccessor contextAccessor,
        IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory, 
        IOptions<IdentityOptions> optionsAccessor, 
        ILogger<SignInManager<ApplicationUser>> logger, 
        IAuthenticationSchemeProvider schemes, 
        IUserConfirmation<ApplicationUser> confirmation,
        SecurityHelperUtil userHelperService) : 
        base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
    {
        _userHelperService = userHelperService;
    }

    public override Task SignInAsync(ApplicationUser user, AuthenticationProperties authenticationProperties, string authenticationMethod = null)
    {
        return base.SignInAsync(user, authenticationProperties, authenticationMethod);
    }

    public override Task SignInAsync(ApplicationUser user, bool isPersistent, string authenticationMethod = null)
    {
        return base.SignInAsync(user, isPersistent, authenticationMethod);
    }

    public override Task<SignInResult> PasswordSignInAsync(ApplicationUser user, string password, bool isPersistent, bool lockoutOnFailure)
    {
        return base.PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure);
    }

    public override Task<SignInResult> PasswordSignInAsync(string userName, string password, bool isPersistent, bool lockoutOnFailure)
    {
        return base.PasswordSignInAsync(userName, password, isPersistent, lockoutOnFailure);
    }


    //public override async Task<SignInResult> SignInAsync(
    //    string userName,
    //    string password,
    //    bool isPersistent,
    //    bool lockoutOnFailure)
    //{
    //var user = await _userManager.FindByNameAsync(userName);
    //if (user == null) return SignInResult.Failed;

    //var result = await CheckPasswordSignInAsync(user, password, lockoutOnFailure);
    //if (!result.Succeeded) return result;

    //string passwordHash = user.PasswordHash;
    //string phsk = _userHelperService.DecryptPHSK(user.PersonalHashKeyLockedByPassword, passwordHash);

    //if (string.IsNullOrEmpty(phsk))
    //{
    //    Logger.LogError("PHSK decryption failed for user {UserId}", user.Id);
    //    return SignInResult.Failed;
    //}

    //user.UserHashKey = phsk;
    //await SignInAsync(user, isPersistent);

    //return SignInResult.Success;
    //    return SignInResult.Failed;
    //}
}