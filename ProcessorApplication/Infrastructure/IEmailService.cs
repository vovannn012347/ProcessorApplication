

using Common.Interfaces.Menu;

namespace ProcessorApplication.Infrastructure;

public interface IEmailService
{
    Task SendEmailAsync(string to, string subject, string htmlMessage);

    // Properties to expose health state safely
    bool IsHealthy { get; }
    string HealthStatus { get; }
    string SmtpHost { get; }
    DateTime LastChecked { get; }

    Task VerifyConnectionAsync();
    void NotifyUserDataAccess(string userEmail, string topic, string message);
}

