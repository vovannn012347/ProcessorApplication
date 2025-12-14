using System.Text.Json;

using Common.Attributes;
using Common.Interfaces;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using ProcessorApplication.Attributes;
using ProcessorApplication.Database.Models;
using ProcessorApplication.Models.Settings;
using ProcessorApplication.Models.User;
using ProcessorApplication.Services;
using ProcessorApplication.Services.User;
using ProcessorApplication.ViewModels.Account;

namespace ProcessorApplication.Controllers;

[Route("/Main/Account")]
[AllowAnonymous]
public class AccountController : Controller
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ProcessorApplicationUserManager _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly ILogger<AccountController> _logger;

    public AccountController(
        IServiceProvider serviceProvider,
        ProcessorApplicationUserManager userManager,
        SignInManager<ApplicationUser> signInManager,
        RoleManager<IdentityRole> roleManager,
        ILogger<AccountController> logger)
    {
        _serviceProvider = serviceProvider;
        _userManager = userManager;
        _signInManager = signInManager;
        _roleManager = roleManager;
        _logger = logger;
    }

    [Route("SessionTimeout")]
    public IActionResult SessionTimeout(string returnUrl)
    {
        return View(model:returnUrl);
    }


    [HttpGet("Register")]
    public IActionResult Register()
    {
        return View(new RegisterViewModel());
    }

    [HttpPost("Register")]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        if (!ModelState.IsValid) { 
            return View(model);
        }
        var passwordValidator = _userManager.PasswordValidators.First();

        var uniqueUserName = await _userManager.GenerateUniqueUsername(model.Name, model.Surname);

        var tempUser = new ApplicationUser { UserName = model.Name };
        var passwordValidationResult = await passwordValidator.ValidateAsync(_userManager, 
            tempUser, 
            model.Password);

        if (!passwordValidationResult.Succeeded)
        {
            foreach (var error in passwordValidationResult.Errors)
            {
                ModelState.AddModelError("Password", error.Description);
            }
            return View(model);
        }

        var user = new ApplicationUser
        {
            UserName = uniqueUserName,
            Name = model.Name,
            Surname = model.Surname,
            DisplayNickname = model.DisplayNickname ?? 
            (!string.IsNullOrWhiteSpace($"{model.Name} {model.Surname}") ? $"{model.Name} {model.Surname}" : uniqueUserName),
            IsEncrypted = false
        };

        var result = await _userManager.CreateAsync(user, model.Password);

        if (result.Succeeded)
        {
            user = await _userManager.FindByIdAsync(user.Id);
            await _signInManager.SignInAsync(user, isPersistent: false);
            return Redirect("/Main/Profile/Index");
        }

        foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
        return View(model);
    }

    [HttpPost("RegisterFromFile")]
    public async Task<IActionResult> RegisterFromFile(
        IFormFile profileFile, 
        string password, 
        string confirmPassword)
    {
        if (profileFile == null)
        {
            ModelState.AddModelError("", "Profile file is required.");
            return View();
        }

        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(confirmPassword))
        {
            ModelState.AddModelError("Password", "Passwords must be present.");
            return View();
        }

        if (password != confirmPassword)
        {
            ModelState.AddModelError("Password", "Passwords must be equal.");
            return View();
        }

        try
        {
            using var stream = new StreamReader(profileFile.OpenReadStream());
            var json = await stream.ReadToEndAsync();
            var model = JsonSerializer.Deserialize<ProfileExportModel>(json);

            var passwordValidator = _userManager.PasswordValidators.First();

            var uniqueUserName = model.UserName;
            if (_userManager.FindByNameAsync(uniqueUserName) != null)
            {
                ModelState.AddModelError(string.Empty, "User with this identifier already exists");
            }

            var tempUser = new ApplicationUser { UserName = model.Name };
            var passwordValidationResult = await passwordValidator.ValidateAsync(_userManager,
                tempUser,
                password);

            if (!passwordValidationResult.Succeeded)
            {
                foreach (var error in passwordValidationResult.Errors)
                {
                    ModelState.AddModelError("Password", error.Description);
                }
                return View(model);
            }

            var user = new ApplicationUser
            {
                UserName = uniqueUserName,
                Name = model.Name,
                Surname = model.Surname,
                DisplayNickname = model.DisplayNickname ??
                (!string.IsNullOrWhiteSpace($"{model.Name} {model.Surname}") ? $"{model.Name} {model.Surname}" : uniqueUserName),
                EncryptionKey = model.PersonalHashKey,
                IsEncrypted = false
            };

            var result = await _userManager.CreateAsync(user, password);

            if (result.Succeeded)
            {
                user = await _userManager.FindByIdAsync(user.Id);
                await _signInManager.SignInAsync(user, isPersistent: false);
                return Redirect("/Main/Profile/Index");
            }

            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return View("Register");
        }
        catch (Exception ex)
        {
            ModelState.AddModelError("", "Import failed: " + ex.Message);
            return View();
        }
    }


    [HttpGet("Login")]
    public IActionResult Login(string returnUrl = "") 
    { 
        return View(new LoginViewModel
        {
            ReturnUrl = returnUrl
        });
    }
    
    [HttpPost("Login")]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) {
            return View(model); 
        }  

        var result = await _signInManager.PasswordSignInAsync(model.UserName, model.Password, false, false);
        if (result.Succeeded)
        {
            //password is correct
            var user = await _userManager.FindByNameAsync(model.UserName);

            if (user != null)
            {              
                var key = SecurityHelperUtil.DecryptData(user.PersonalHashKeyLockedByPassword, model.Password);
                var userName = SecurityHelperUtil.DecryptData(user.UserIdLockedByPHSK, key);
                if(userName != user.UserName)
                {
                    _logger.LogWarning($"user {user.UserName} encrypted hash is iccorect, recrypting");

                    user.UserIdLockedByPHSK = SecurityHelperUtil.EncryptData(user.UserName, key);// AesEncryptionHelper.Encrypt(, formattedUserHashKey);
                    user.LastLogin = DateTime.UtcNow;

                    await _userManager.UpdateAsync(user);
                }

                //for use in other controllers
                this.HttpContext.Session.SetString("UselessInfoHash", key);

                if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
                {
                    return Redirect(model.ReturnUrl);
                }
                else
                {
                    return Redirect("/Main/Home/Dashboard");
                }
            }
        }

        //todo: more verbose login
        ModelState.AddModelError(string.Empty, "Invalid login attempt.");
        return View(model);
    }

    [HttpPost("LoginFromFile")]
    //redo
    public async Task<IActionResult> LoginFromFile(
        IFormFile profileFile,
        string password,
        string returnUrl = "")
    {
        var loginModel = new LoginViewModel
        {
            ReturnUrl = returnUrl
        };

        if (profileFile == null)
        {
            ModelState.AddModelError("", "Profile file is required.");
            return View("Login", loginModel);
        }

        if (string.IsNullOrEmpty(password))
        {
            ModelState.AddModelError("Password", "Password must be present.");
            return View("Login", loginModel);
        }

        try
        {
            using var stream = new StreamReader(profileFile.OpenReadStream());
            var json = await stream.ReadToEndAsync();
            var model = JsonSerializer.Deserialize<ProfileExportModel>(json);

            if (model == null) throw new Exception("Invalid file format");

            var user = await _userManager.FindByNameAsync(model.UserName);
            if (user == null)
            {
                ModelState.AddModelError("", "User not found.");
                return View("Login", loginModel);
            }

            var decryptedPhsk = SecurityHelperUtil.DecryptData(user.PersonalHashKeyLockedByPassword, password);

            if (string.IsNullOrEmpty(decryptedPhsk))
            {
                ModelState.AddModelError("", "Invalid Password for this file.");
                return View("Login", loginModel);
            }

            var fileDecryptedId = SecurityHelperUtil.DecryptData(user.UserIdLockedByPHSK, decryptedPhsk);

            if (fileDecryptedId != user.UserName)
            {
                ModelState.AddModelError("", "File integrity check failed.");
                return View("Login", loginModel);
            }

            // Check Database Lock (Double Validity)
            bool isDbSyncValid = false;
            try
            {
                var dbDecryptedId = SecurityHelperUtil.DecryptData(user.UserIdLockedByPHSK, decryptedPhsk);
                if (dbDecryptedId == user.UserName) isDbSyncValid = true;
            }
            catch { isDbSyncValid = false; }

            if (!isDbSyncValid)
            {
                _logger.LogWarning($"User {user.UserName} DB lock mismatch.");
            }

            //todo: add 2fa
            await _signInManager.SignInAsync(user, isPersistent: false);

            user.LastLogin = DateTime.UtcNow;
            await _userManager.UpdateAsync(user);

            this.HttpContext.Session.SetString("UselessInfoHash", decryptedPhsk);


            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return Redirect(returnUrl);
            }
            else
            {
                return Redirect("/Main/Home/Dashboard");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File login error.");
            ModelState.AddModelError(string.Empty, "Invalid login attempt or corrupted file.");
            // Return the specific Login view so the user can try again
            return View("Login", loginModel);
        }
    }

    [HttpPost("Logout")]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        this.HttpContext.Session.Clear();
        return Redirect("/Main/Home/Dashboard");
    }

    [LocalhostOnly]
    [HttpGet("AdminRegister")]
    public IActionResult AdminRegister()
    {
        return View(new AdminRegisterViewModel());
    }
    [LocalhostOnly]
    [HttpPost("AdminRegister")]
    public async Task<IActionResult> AdminRegister(AdminRegisterViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }
        var passwordValidator = _userManager.PasswordValidators.First();

        var tempUser = new ApplicationUser { UserName = model.Name };
        var passwordValidationResult = await passwordValidator.ValidateAsync(_userManager,
            tempUser,
            model.Password);

        if (!passwordValidationResult.Succeeded)
        {
            foreach (var error in passwordValidationResult.Errors)
            {
                ModelState.AddModelError("Password", error.Description);
            }
            return View(model);
        }

        var user = new ApplicationUser
        {
            UserName = "Admin",
            Name = model.Name,
            Email = model.Email,
            Surname = model.Surname,
            DisplayNickname = "Admin",
            IsEncrypted = false
        };

        var result = await _userManager.CreateAsync(user, model.Password);

        if (result.Succeeded)
        {
            var settings = _serviceProvider.GetService<IOptionsMonitor<EmailSettings>>() ?? null;
            string currentEmail = settings != null ? settings.Get(MainModule.MainId).Identity.Email : ""; 

            if (!string.IsNullOrEmpty(model.Email) && string.IsNullOrEmpty(currentEmail))
            {
                var settingsService = _serviceProvider.GetRequiredService<ISettingService>();
                await settingsService.SetAsync(MainModule.MainId, "EmailSettings:Email", model.Email);
            }


            user = await _userManager.FindByIdAsync(user.Id);
            await _userManager.AddToRoleAsync(user, "Admin");

            await _signInManager.SignInAsync(user, isPersistent: false);

            var adminState = _serviceProvider.GetRequiredService<AdminSetupState>();
            adminState.SetAdminConfigured();

            this.HttpContext.Session.SetString("UselessInfoHash", user.EncryptionKey);

            return Redirect("/Main/Profile/Index");
        }

        foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
        return View(model);
    }

}
