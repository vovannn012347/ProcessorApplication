namespace ProviderlessModule.Models;

internal class QrStatusResult
{
    public bool TunnelActive { get; set; }
    public bool RegistyActive { get; set; }
    public string Url { get; set; }
    public string StartTime { get; set; }
    public string ProviderName { get; set; }
    public string LastError { get; set; }
}