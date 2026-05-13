namespace ProcessingModule.Models.Views;


// Support classes for the composite view
public class ScriptDetailsViewModel
{
    public List<ParameterDetailViewModel> Inputs { get; set; } = new();
    public List<ParameterDetailViewModel> Outputs { get; set; } = new();
}

public class ParameterDetailViewModel
{
    public string DisplayName { get; set; }
    public string Type { get; set; }
}