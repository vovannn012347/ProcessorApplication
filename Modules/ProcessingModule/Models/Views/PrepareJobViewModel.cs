using ProcessingModule.Database.Models;

namespace ProcessingModule.Models.Views;

public class PrepareJobViewModel
{
    public string ScriptIds { get; set; } = string.Empty;
    public List<ScriptIndex> SelectedScripts { get; set; } = new();
    public List<GroupedInputViewModel> GroupedInputs { get; set; } = new();
}

public class GroupedInputViewModel
{
    public string Label { get; set; }
    public string Type { get; set; }
    public string UniqueKey => $"{Label}|{Type}"; // Used for form name binding
    public List<ScriptInputMetadata> Sources { get; set; } = new();
}

public class ScriptInputMetadata
{
    public string ScriptLabel { get; set; }
    public string LocalizedDescription { get; set; }
}