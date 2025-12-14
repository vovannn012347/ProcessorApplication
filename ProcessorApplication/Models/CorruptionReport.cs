using System.ComponentModel.DataAnnotations;

namespace ProcessorApplication.Models;

public record CorruptionReport
{
    public string ChainIdentifier { get; init; }
    public string Reason { get; init; }
    public string ExpectedHash { get; init; }
    public DateTime StampTime { get; init; }
    public string ActualHash { get; init; }
}
