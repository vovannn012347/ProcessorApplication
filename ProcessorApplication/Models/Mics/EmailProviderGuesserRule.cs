namespace ProcessorApplication.Models.Settings;

public class EmailProviderGuesserRule
{
    public List<string> Domains { get; set; } = new();
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = false;
}