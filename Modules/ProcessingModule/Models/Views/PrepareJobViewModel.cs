using System.Globalization;

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

    // First default found among the scripts sharing this input (null = none)
    public string? Default { get; set; }
    // True only if every script sharing this input marks it optional
    public bool Optional { get; set; } = true;
    public bool DefaultChecked => bool.TryParse(Default, out var b) && b;

    // Localized short name ("<text_label>"), falls back to the raw label
    public string DisplayName { get; set; } = string.Empty;
    // Allowed values (dropdown); empty = free input
    public List<ChoiceOptionViewModel> Choices { get; set; } = new();
    public decimal? Min { get; set; }
    public decimal? Max { get; set; }
    public decimal? Step { get; set; }

    public string? DefaultText => string.IsNullOrEmpty(Default) ? null
        : Type == "boolean" ? (DefaultChecked ? "on" : "off")
        : Choices.FirstOrDefault(c => string.Equals(c.Value, Default, StringComparison.OrdinalIgnoreCase))?.Text ?? Default;

    public string? RangeText => (Min, Max) switch
    {
        (null, null) => null,
        (decimal lo, null) => $"≥ {Fmt(lo)}",
        (null, decimal hi) => $"≤ {Fmt(hi)}",
        (decimal lo, decimal hi) => $"{Fmt(lo)} – {Fmt(hi)}"
    };

    // Invariant culture: number inputs always use '.' regardless of the server culture
    public static string Fmt(decimal? d) => d?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}

public class ChoiceOptionViewModel
{
    public string Value { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public class ScriptInputMetadata
{
    public string ScriptLabel { get; set; }
    public string LocalizedDescription { get; set; }
    // Longer help text ("<text_label>.description"); null when the script doesn't provide one
    public string? Help { get; set; }
}
