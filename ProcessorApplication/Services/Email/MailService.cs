using MailKit.Net.Smtp;
using MailKit.Security;

using Microsoft.Extensions.Options;

using MimeKit;

using ProcessorApplication.Configuration.Settings;
using ProcessorApplication.Infrastructure;

namespace ProcessorApplication.Services.Email;

public class EmailService : IEmailService, IDisposable
{
    private readonly ILogger<EmailService> _logger;
    private readonly IOptionsMonitor<EmailSettings> _optionsMonitor;
    private readonly IDisposable _settingsChangeListener;

    private readonly object _stateLock = new();
    private bool _isHealthy = false;
    private string _healthStatus = "Initializing...";
    private DateTime _lastChecked = DateTime.MinValue;

    public EmailService(
        IOptionsMonitor<EmailSettings> optionsMonitor,
        ILogger<EmailService> logger)
    {
        _logger = logger;
        _optionsMonitor = optionsMonitor;

        _settingsChangeListener = _optionsMonitor.OnChange((settings, name) =>
        {
            _logger.LogInformation("Email settings updated via IOptionsMonitor. Re-verifying...");
            // Fire and forget (or await properly if context allows, but OnChange is void)
            Task.Run(() => VerifyConnectionAsync());
        });
    }

    // Thread-safe reads
    public bool IsHealthy
    {
        get { lock (_stateLock) { return _isHealthy; } }
    }

    public string HealthStatus
    {
        get { lock (_stateLock) { return _healthStatus; } }
    }

    public DateTime LastChecked
    {
        get { lock (_stateLock) { return _lastChecked; } }
    }
    private async Task AuthenticateClientAsync(ISmtpClient client, EmailSettings settings)
    {
        // For Certificate/KeyPair Auth, we often need to present the cert during the TLS handshake
        // which happens inside ConnectAsync. If your logic requires adding certs, 
        // you might need to load them into client.ClientCertificates before Connect.
        // However, here is the standard Authentication logic:

        switch (settings.Mode)
        {
            case EmailAuthMode.PasswordAuth:
                if (!string.IsNullOrEmpty(settings.Auth.PasswordAuth.Password))
                {
                    await client.AuthenticateAsync(settings.Identity.Email, settings.Auth.PasswordAuth.Password);
                }
                break;

            case EmailAuthMode.AppPasswordAuth:
                if (!string.IsNullOrEmpty(settings.Auth.AppPasswordAuth.AppPassword))
                {
                    // Treat AppPassword exactly like a standard password
                    await client.AuthenticateAsync(settings.Identity.Email, settings.Auth.AppPasswordAuth.AppPassword);
                }
                break;

            case EmailAuthMode.OAuthAuth:
                if (!string.IsNullOrEmpty(settings.Auth.OAuthAuth.AccessToken))
                {
                    // SASL XOAUTH2
                    var oauth2 = new SaslMechanismOAuth2(settings.Identity.Email, settings.Auth.OAuthAuth.AccessToken);
                    await client.AuthenticateAsync(oauth2);
                }
                break;

            case EmailAuthMode.ApiKeyAuth:
                if (!string.IsNullOrEmpty(settings.Auth.ApiKeyAuth.ApiKey))
                {
                    // Standard pattern for SendGrid, Mailgun, etc. User is usually "apikey"
                    await client.AuthenticateAsync("apikey", settings.Auth.ApiKeyAuth.ApiKey);
                }
                break;

            case EmailAuthMode.ServiceAccountAuth:
                // NOTE: Service Accounts usually require generating a JWT Signed Token 
                // using a specific library (e.g. Google.Apis.Auth). 
                // Assuming here that 'ServiceAccountKey' contains a pre-generated token 
                // or specific logic is needed per provider.
                if (!string.IsNullOrEmpty(settings.Auth.ServiceAccountAuth.ServiceAccountKey))
                {
                    var sasl = new SaslMechanismOAuth2(settings.Auth.ServiceAccountAuth.DelegatedUser, settings.Auth.ServiceAccountAuth.ServiceAccountKey);
                    await client.AuthenticateAsync(sasl);
                }
                break;

            case EmailAuthMode.CertificateAuth:
                // This usually implies Mutual TLS (mTLS). 
                // The certificate must be added to client.ClientCertificates BEFORE ConnectAsync usually,
                // or used here if the SMTP server supports SASL EXTERNAL.
                // This is a placeholder for SASL EXTERNAL:
                // await client.AuthenticateAsync(new SaslMechanismExternal(uri: null));
                break;

            case EmailAuthMode.KeyPairAuth:
                // Custom implementation depending on your specific protocol
                break;
        }
    }

    public async Task VerifyConnectionAsync()
    {
        var settings = _optionsMonitor.CurrentValue;
        bool success = false;
        string message = "";

        // 2a. Pre-check: Validation
        if (string.IsNullOrEmpty(settings.Server.SmtpHost))
        {
            lock (_stateLock) { _isHealthy = false; _healthStatus = "SMTP Host is missing."; }
            return;
        }

        try
        {
            using var client = new SmtpClient();
            client.Timeout = 8000; // 8 seconds

            // 2b. Configure Certificates for TLS if needed (for CertificateAuth)
            if (settings.Mode == EmailAuthMode.CertificateAuth && !string.IsNullOrEmpty(settings.Auth.CertificateAuth.CertificatePath))
            {
                if (System.IO.File.Exists(settings.Auth.CertificateAuth.CertificatePath))
                {
                    var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                        settings.Auth.CertificateAuth.CertificatePath,
                        settings.Auth.CertificateAuth.CertificatePassword);
                    client.ClientCertificates.Add(cert);
                }
            }

            // 2c. Connect
            var socketOptions = settings.Server.UseSsl
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await client.ConnectAsync(settings.Server.SmtpHost, settings.Server.SmtpPort, socketOptions);

            // 2d. Authenticate (using the helper)
            await AuthenticateClientAsync(client, settings);

            await client.DisconnectAsync(true);
            success = true;
            message = $"Connected to {settings.Server.SmtpHost} successfully.";
        }
        catch (Exception ex)
        {
            success = false;
            message = ex.Message;
            _logger.LogWarning("Email check failed: {Error}", ex.Message);
        }

        lock (_stateLock)
        {
            _isHealthy = success;
            _healthStatus = message;
            _lastChecked = DateTime.UtcNow;
        }
    }

    // 3. The Sending Logic
    public async Task SendEmailAsync(string to, string subject, string htmlMessage)
    {
        // Quick Health Check
        bool healthy;
        lock (_stateLock) { healthy = _isHealthy; }

        // Optional: If you want to force sending even if health check is stale, remove this block.
        // But for security clearance, this block is vital.
        if (!healthy)
        {
            // Try one last-ditch explicit check before failing? 
            // await VerifyConnectionAsync();
            // Lock again to check?
            // For now, fail fast:
            throw new InvalidOperationException($"Cannot send email. Service Status: {_healthStatus}");
        }

        var settings = _optionsMonitor.CurrentValue;

        var message = new MimeMessage();
        var fromName = !string.IsNullOrEmpty(settings.Identity.FromName) ? settings.Identity.FromName : "System";
        var fromEmail = !string.IsNullOrEmpty(settings.Identity.Email) ? settings.Identity.Email : settings.Identity.Email;

        message.From.Add(new MailboxAddress(fromName, fromEmail));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlMessage }.ToMessageBody();

        using var client = new SmtpClient();

        // Apply Certs for TLS if needed
        if (settings.Mode == EmailAuthMode.CertificateAuth && System.IO.File.Exists(settings.Auth.CertificateAuth.CertificatePath))
        {
            var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                settings.Auth.CertificateAuth.CertificatePath,
                settings.Auth.CertificateAuth.CertificatePassword);
            client.ClientCertificates.Add(cert);
        }

        var socketOptions = settings.Server.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;

        await client.ConnectAsync(settings.Server.SmtpHost, settings.Server.SmtpPort, socketOptions);

        // Re-use the exact same logic
        await AuthenticateClientAsync(client, settings);

        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    public void Dispose()
    {
        _settingsChangeListener?.Dispose();
    }

    //concrete implementations

    public void NotifyUserDataAccess(string userEmail, string topic, string message)
    {
        SendEmailAsync(userEmail, topic, message);
    }
}