using Common.Interfaces;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;

using ProcessorApplication.Models.Settings;
using ProcessorApplication.Utils;
using ProcessorApplication.ViewModels;

namespace ProcessorApplication.Controllers;

[Route("Main/Settings")]
[Authorize(Policy = "AdminLocalPolicy")]
public class SettingsController : Controller
{
    private readonly ILogger<SettingsController> _logger;
    private readonly ISettingService _settings; //here save happens
    private readonly IOptionsMonitor<SecuritySettings> _securitySettings;
    private readonly IOptionsMonitor<EmailSettings> _emailSettings;
    private readonly IOptionsSnapshot<List<EmailProviderGuesserRule>> _providerRules;

    public SettingsController(
        ILogger<SettingsController> logger,
        ISettingService settingsService,
        IOptionsMonitor<SecuritySettings> securitySettings,
        IOptionsMonitor<EmailSettings> emailSettings,
        IOptionsSnapshot<List<EmailProviderGuesserRule>> providerRules)
    {
        _logger = logger;
        _settings = settingsService;
        _securitySettings = securitySettings; 
        _emailSettings = emailSettings;
        _providerRules = providerRules;
    }

    [Route("Index")] // Catches /Home/Dashboard
    public IActionResult Index()
    {
        var model = new SettingsViewModel
        {
            Security = _securitySettings.Get(MainModule.MainId),
            Email = _emailSettings.Get(MainModule.MainId)
        };

        if (Request.IsAjaxRequest())
        {
            return PartialView(model);
        }

        return View(model);
    }

    [HttpPost("ProcessAllSettings")]
    public async Task<IActionResult> ProcessAllSettings([FromForm] SettingsViewModel model)
    {
        if (!TryValidateModel(model.Security)||
            !TryValidateModel(model.Email))
        {
            return PartialView("Index", model);
        }

        TryValidateSecurity(model, ModelState);
        TryValidateEmail(model, ModelState);

        if (!ModelState.IsValid)
        {
            return PartialView("Index", model);
        }

        try
        {
            _settings.SetAutoUpdate(false);

            await SaveSecurity(model);
            await SaveEmail(model);

            _settings.SetAutoUpdate(true);
            _settings.ForceUpdateOptionsMonitor();

            ViewData["GlobalMessage"] = "Settings saved successfully and configuration reloaded!";
            ViewData["MessageType"] = "Success";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save all settings.");
            ViewData["GlobalMessage"] = "Failed to save settings: " + ex.Message;
            ViewData["MessageType"] = "Error";
        }

        // Return the container view to show the messages.
        return PartialView("Index", model);
    }
    private void TryValidateSecurity(SettingsViewModel model, ModelStateDictionary modelState)
    {
        try
        {
            var time = TimeSpan.FromHours(model.Security.HashStampGenerationPeriod);
        }
        catch
        {
            ModelState.AddModelError($"Security.{nameof(model.Security.HashStampGenerationPeriod)}", "Invalid TimeSpan length.");
        }

        if (model.Security.HashStampBackupEnabled && string.IsNullOrWhiteSpace(model.Security.HashStampBackupFilePath))
        {
            ModelState.AddModelError($"Security.{nameof(model.Security.HashStampBackupFilePath)}", "Backup path is required if backup is enabled.");
        }

        if (model.Security.RecordDecipherLogging && string.IsNullOrWhiteSpace(model.Security.RecordDecipherLogPath))
        {
            ModelState.AddModelError($"Security.{nameof(model.Security.RecordDecipherLogPath)}", "Log path is required if accss logging is enabled.");
        }
    }
    private async Task SaveSecurity(SettingsViewModel model)
    {
        var security = model.Security;

        await _settings.SetAsync(MainModule.MainId, $"{nameof(SecuritySettings)}:{nameof(SecuritySettings.HashStampGenerationPeriod)}", security.HashStampGenerationPeriod);
        await _settings.SetAsync(MainModule.MainId, $"{nameof(SecuritySettings)}:{nameof(SecuritySettings.HashStampBackupEnabled)}", security.HashStampBackupEnabled);
        await _settings.SetAsync(MainModule.MainId, $"{nameof(SecuritySettings)}:{nameof(SecuritySettings.HashStampBackupFilePath)}", security.HashStampBackupFilePath);
        await _settings.SetAsync(MainModule.MainId, $"{nameof(SecuritySettings)}:{nameof(SecuritySettings.RecordDecipherLogging)}", security.RecordDecipherLogging);
        await _settings.SetAsync(MainModule.MainId, $"{nameof(SecuritySettings)}:{nameof(SecuritySettings.RecordDecipherLogPath)}", security.RecordDecipherLogPath);
    }


    private void TryValidateEmail(SettingsViewModel model, ModelStateDictionary modelState)
    {
        var emailParams = model.Email;

        // 1. SMART AUTO-FILL
        // If the user entered an email but NO host, try to guess the host before we validate.
        if (!string.IsNullOrWhiteSpace(emailParams.Identity.Email) && string.IsNullOrWhiteSpace(emailParams.Server.SmtpHost))
        {
            var (host, port, ssl) = InferServerSettings(emailParams.Identity.Email);
            if (!string.IsNullOrEmpty(host))
            {
                emailParams.Server.SmtpHost = host;
                emailParams.Server.SmtpPort = port;
                emailParams.Server.UseSsl = ssl;
                // We also clear any "Required" errors that might have been generated by TryValidateModel for these fields
                // (Though SmtpHost isn't marked [Required] in your model, this is good practice)
                modelState.Remove("Email.Server.SmtpHost");
            }
        }

        // 2. Validate Server Basics
        if (string.IsNullOrWhiteSpace(emailParams.Server.SmtpHost))
        {
            modelState.AddModelError("Email.Server.SmtpHost", "SMTP Host is required.");
        }

        // 3. Conditional Validation based on Auth Mode
        // We only validate the fields relevant to the selected mode.
        switch (emailParams.Mode)
        {
            case EmailAuthMode.PasswordAuth:
                if (string.IsNullOrWhiteSpace(emailParams.Auth.PasswordAuth.Password))
                    modelState.AddModelError("Email.Auth.PasswordAuth.Password", "Password is required for Standard Auth.");
                break;

            case EmailAuthMode.AppPasswordAuth:
                if (string.IsNullOrWhiteSpace(emailParams.Auth.AppPasswordAuth.AppPassword))
                    modelState.AddModelError("Email.Auth.AppPasswordAuth.AppPassword", "App Password is required.");
                break;

            case EmailAuthMode.OAuthAuth:
                if (string.IsNullOrWhiteSpace(emailParams.Auth.OAuthAuth.AccessToken))
                    modelState.AddModelError("Email.Auth.OAuthAuth.AccessToken", "Access Token is required for OAuth.");
                break;

            case EmailAuthMode.ApiKeyAuth:
                if (string.IsNullOrWhiteSpace(emailParams.Auth.ApiKeyAuth.ApiKey))
                    modelState.AddModelError("Email.Auth.ApiKeyAuth.ApiKey", "API Key is required.");
                break;

            case EmailAuthMode.ServiceAccountAuth:
                if (string.IsNullOrWhiteSpace(emailParams.Auth.ServiceAccountAuth.ServiceAccountKey))
                    modelState.AddModelError("Email.Auth.ServiceAccountAuth.ServiceAccountKey", "Service Key is required.");
                break;

            case EmailAuthMode.CertificateAuth:
                if (string.IsNullOrWhiteSpace(emailParams.Auth.CertificateAuth.CertificatePath))
                    modelState.AddModelError("Email.Auth.CertificateAuth.CertificatePath", "Certificate Path is required.");
                break;

            case EmailAuthMode.KeyPairAuth:
                if (string.IsNullOrWhiteSpace(emailParams.Auth.KeyPairAuth.PrivateKeyPath))
                    modelState.AddModelError("Email.Auth.KeyPairAuth.PrivateKeyPath", "Private Key Path is required.");
                break;
        }
    }

    private async Task SaveEmail(SettingsViewModel model)
    {
        var s = model.Email;
        var area = MainModule.MainId;
        // Assuming your appsettings JSON structure starts with "EmailSettings"
        var root = "EmailSettings";

        // General
        await _settings.SetAsync(area, $"{root}:{nameof(s.Mode)}", s.Mode); // Enum saves as int or string depending on T implementation
        await _settings.SetAsync(area, $"{root}:{nameof(s.HealthCheckPeriodMinutes)}", s.HealthCheckPeriodMinutes);

        // Identity
        await _settings.SetAsync(area, $"{root}:Identity:{nameof(s.Identity.Email)}", s.Identity.Email);
        await _settings.SetAsync(area, $"{root}:Identity:{nameof(s.Identity.FromName)}", s.Identity.FromName);
        await _settings.SetAsync(area, $"{root}:Identity:{nameof(s.Identity.Username)}", s.Identity.Username);

        // Server
        await _settings.SetAsync(area, $"{root}:Server:{nameof(s.Server.SmtpHost)}", s.Server.SmtpHost);
        await _settings.SetAsync(area, $"{root}:Server:{nameof(s.Server.SmtpPort)}", s.Server.SmtpPort);
        await _settings.SetAsync(area, $"{root}:Server:{nameof(s.Server.UseSsl)}", s.Server.UseSsl);

        // Auth - Password
        await _settings.SetAsync(area, $"{root}:Auth:PasswordAuth:{nameof(s.Auth.PasswordAuth.Password)}", s.Auth.PasswordAuth.Password);

        // Auth - AppPassword
        await _settings.SetAsync(area, $"{root}:Auth:AppPasswordAuth:{nameof(s.Auth.AppPasswordAuth.AppPassword)}", s.Auth.AppPasswordAuth.AppPassword);

        // Auth - OAuth
        await _settings.SetAsync(area, $"{root}:Auth:OAuthAuth:{nameof(s.Auth.OAuthAuth.AccessToken)}", s.Auth.OAuthAuth.AccessToken);
        await _settings.SetAsync(area, $"{root}:Auth:OAuthAuth:{nameof(s.Auth.OAuthAuth.RefreshToken)}", s.Auth.OAuthAuth.RefreshToken);
        await _settings.SetAsync(area, $"{root}:Auth:OAuthAuth:{nameof(s.Auth.OAuthAuth.TokenType)}", s.Auth.OAuthAuth.TokenType);
        await _settings.SetAsync(area, $"{root}:Auth:OAuthAuth:{nameof(s.Auth.OAuthAuth.ExpiresAt)}", s.Auth.OAuthAuth.ExpiresAt);

        // Auth - ApiKey
        await _settings.SetAsync(area, $"{root}:Auth:ApiKeyAuth:{nameof(s.Auth.ApiKeyAuth.ApiKey)}", s.Auth.ApiKeyAuth.ApiKey);

        // Auth - ServiceAccount
        await _settings.SetAsync(area, $"{root}:Auth:ServiceAccountAuth:{nameof(s.Auth.ServiceAccountAuth.ServiceAccountKey)}", s.Auth.ServiceAccountAuth.ServiceAccountKey);
        await _settings.SetAsync(area, $"{root}:Auth:ServiceAccountAuth:{nameof(s.Auth.ServiceAccountAuth.DelegatedUser)}", s.Auth.ServiceAccountAuth.DelegatedUser);

        // Auth - Certificate
        await _settings.SetAsync(area, $"{root}:Auth:CertificateAuth:{nameof(s.Auth.CertificateAuth.CertificatePath)}", s.Auth.CertificateAuth.CertificatePath);
        await _settings.SetAsync(area, $"{root}:Auth:CertificateAuth:{nameof(s.Auth.CertificateAuth.CertificatePassword)}", s.Auth.CertificateAuth.CertificatePassword);

        // Auth - KeyPair
        await _settings.SetAsync(area, $"{root}:Auth:KeyPairAuth:{nameof(s.Auth.KeyPairAuth.PrivateKeyPath)}", s.Auth.KeyPairAuth.PrivateKeyPath);
        await _settings.SetAsync(area, $"{root}:Auth:KeyPairAuth:{nameof(s.Auth.KeyPairAuth.PrivateKeyPassword)}", s.Auth.KeyPairAuth.PrivateKeyPassword);
    }

    private (string Host, int Port, bool UseSsl) InferServerSettings(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return ("", 587, false);

        var domain = email.Split('@').LastOrDefault()?.ToLower();
        if (string.IsNullOrEmpty(domain)) return ("", 587, false);

        // search the injected rules
        var rule = _providerRules.Value
            .FirstOrDefault(r => r.Domains.Contains(domain));

        if (rule != null)
        {
            return (rule.SmtpHost, rule.SmtpPort, rule.UseSsl);
        }

        return ("", 587, false);
    }
}