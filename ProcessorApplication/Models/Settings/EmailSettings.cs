using System.ComponentModel.DataAnnotations;

namespace ProcessorApplication.Models.Settings;

public class EmailSettings
{
    [Display(Name = "Authentication Mode")]
    public EmailAuthMode Mode { get; set; } = EmailAuthMode.PasswordAuth;

    [Display(Name = "Health Check Interval (Minutes)")]
    [Range(1, 1440, ErrorMessage = "Interval must be between 1 minute and 24 hours.")]
    public int HealthCheckPeriodMinutes { get; set; } = 5;
    
    public EmailIdentitySettings Identity { get; set; } = new();
    public EmailServerSettings Server { get; set; } = new();
    public EmailAuthSettings Auth { get; set; } = new();
}

public class EmailIdentitySettings
{
    // Often used for the "Reply-To" or specific header identity
    public string Email { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    [Display(Name = "Sender Name")]
    public string FromName { get; set; } = string.Empty;
}

public class EmailServerSettings
{
    [Display(Name = "SMTP Host")]
    public string SmtpHost { get; set; } = string.Empty;
    [Display(Name = "SMTP Port")]
    public int SmtpPort { get; set; } = 587;
    [Display(Name = "Use SSL/TLS")]
    public bool UseSsl { get; set; } = true;
}

// Container for all auth strategies
public class EmailAuthSettings
{
    public PasswordAuthSettings PasswordAuth { get; set; } = new();
    public AppPasswordAuthSettings AppPasswordAuth { get; set; } = new();
    public OAuthAuthSettings OAuthAuth { get; set; } = new();
    public ApiKeyAuthSettings ApiKeyAuth { get; set; } = new();
    public ServiceAccountAuthSettings ServiceAccountAuth { get; set; } = new();
    public CertificateAuthSettings CertificateAuth { get; set; } = new();
    public KeyPairAuthSettings KeyPairAuth { get; set; } = new();
}

// Sub-settings for specific modes
public class PasswordAuthSettings { public string Password { get; set; } = string.Empty; }
public class AppPasswordAuthSettings { public string AppPassword { get; set; } = string.Empty; }
public class OAuthAuthSettings
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string TokenType { get; set; } = "Bearer";
    public string ExpiresAt { get; set; } = string.Empty;
}
public class ApiKeyAuthSettings { public string ApiKey { get; set; } = string.Empty; }
public class ServiceAccountAuthSettings { public string ServiceAccountKey { get; set; } = string.Empty; public string DelegatedUser { get; set; } = string.Empty; }
public class CertificateAuthSettings { public string CertificatePath { get; set; } = string.Empty; public string CertificatePassword { get; set; } = string.Empty; }
public class KeyPairAuthSettings { public string PrivateKeyPath { get; set; } = string.Empty; public string PrivateKeyPassword { get; set; } = string.Empty; }

// Enum for Dropdown - Supports Localization via ResourceType if needed
public enum EmailAuthMode
{
    [Display(Name = "Standard Password")]
    PasswordAuth,
    [Display(Name = "App-Specific Password")]
    AppPasswordAuth,
    [Display(Name = "OAuth 2.0")]
    OAuthAuth,
    [Display(Name = "API Key")]
    ApiKeyAuth,
    [Display(Name = "Service Account")]
    ServiceAccountAuth,
    [Display(Name = "Client Certificate")]
    CertificateAuth,
    [Display(Name = "Key Pair (Custom)")]
    KeyPairAuth
}