namespace ProcessingModule.Models.Views;
public class SubJobDetailsViewModel
{
    public Guid SubJobId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ResultMessage { get; set; }
    public string? SummaryJson { get; set; }

    // Scalar inputs (strings, numbers, etc.)
    public List<ParameterValueViewModel> ScalarInputs { get; set; } = new();

    // Physical files used as input (Resolved to FileController URLs)
    public List<ArtifactViewModel> InputArtifacts { get; set; } = new();

    // Scalar outputs from direct_output.json
    public List<DirectOutputViewModel> DirectOutputs { get; set; } = new();

    // Files explicitly defined in the script manifest
    public List<ArtifactViewModel> ExplicitArtifacts { get; set; } = new();

    // Files found in file_output.json (internal/intermediate)
    public List<ArtifactViewModel> InternalArtifacts { get; set; } = new();

}

public class ParameterValueViewModel
{
    public string Label { get; set; } = string.Empty;
    public string LocalizedLabel { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class DirectOutputViewModel : ParameterValueViewModel { }

public class ArtifactViewModel
{
    public string FileName { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string LocalizedType { get; set; } = string.Empty;
    public bool IsImage { get; set; }
    public string Extension { get; set; } = string.Empty;
}