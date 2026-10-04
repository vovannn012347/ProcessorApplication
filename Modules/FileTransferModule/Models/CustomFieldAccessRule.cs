namespace FileTransferModule.Models;

public enum FieldRuleAction
{
    Allow,
    Deny
}

/// <summary>
/// One access-list rule for a custom field (or the description) received from another host via
/// CatalogGetFileInfo. Never applied to your own records - those are edited freely
/// (FileArchiveService.UpdateRecordSettingsAsync has no such restriction).
///
/// FieldNamePattern is a regex matched against the field name ("description" is the reserved name
/// for the free-text description, everything else is a custom-field key). ValuePattern, if set, is
/// also matched against the field's value - this is what lets a rule reject/allow based on content,
/// e.g. a value-pattern of "^.{0,200}$" only allows values up to 200 characters.
///
/// Rules are evaluated in order; the first rule whose FieldNamePattern matches the name AND whose
/// ValuePattern (if any) matches the value decides Action for that field. A field matched by no
/// rule falls back to FileSharingSettings.CustomFieldDefaultAction.
/// </summary>
public class CustomFieldAccessRule
{
    public string FieldNamePattern { get; set; } = string.Empty;
    /// <summary>Optional. Null or empty matches any value.</summary>
    public string? ValuePattern { get; set; }
    public FieldRuleAction Action { get; set; } = FieldRuleAction.Deny;
}
