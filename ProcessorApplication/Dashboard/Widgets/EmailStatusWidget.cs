using Common.Interfaces;

using ProcessorApplication.Infrastructure;

namespace ProcessorApplication.Dashboard.Widgets;

public class EmailStatusWidget : IDashboardWidget, IDisposable
{
    private readonly IEmailService _emailService;
    public EmailStatusWidget(IEmailService emailService)
    {
        _emailService = emailService;
    }

    public WidgetManifest Manifest => new WidgetManifest
    {
        Id = "main-email-status",
        Name = "Dashboard_Email_Widget_Header",
        IconClass = "fa-solid fa-envelope",
        Roles = "Admin",
        ViewPath = "~/Views/DashboardWidgets/_EmailStatus.cshtml",
        ScriptPath = "/js/dashboard/widgets/email-widget.js",
    };

    public void Dispose()
    {
        //nothing to dipose of
    }

    public async Task<object> GetUpdateAsync()
    {
        // 0: OK, 1: Warning, 2: Error
        int stateCode = _emailService.IsHealthy ? 0 : 1;
        if (string.IsNullOrEmpty(_emailService.SmtpHost)) stateCode = 2;

        return new
        {
            state = stateCode,
            address = _emailService.SmtpHost,
            statusText = _emailService.HealthStatus,
            lastChecked = DateTime.Now.ToString("HH:mm:ss")
        };
    }

}