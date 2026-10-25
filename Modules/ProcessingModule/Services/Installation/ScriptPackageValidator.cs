using System.Globalization;
using System.Text.RegularExpressions;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using ProcessingModule.Infrastructure;
using ProcessingModule.Infrastructure.Util;
using ProcessingModule.Models;

namespace ProcessingModule.Services.Installation;

public sealed class ScriptValidationResult
{
    public ScriptValidationReport Report { get; } = new();

    /// <summary>Parsed manifest, or null if it couldn't be read.</summary>
    public ScriptManifest? Manifest { get; set; }
}

/// <summary>
/// Checks a script folder against what the app and the executor actually rely on.
/// Errors mean the script would fail to index, show a broken form, or fail at run time.
/// Warnings are things that work but look like mistakes (missing translations, hidden inputs).
/// </summary>
public static class ScriptPackageValidator
{
    private static readonly Regex ScriptIdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$", RegexOptions.CultureInvariant);

    private static readonly string[] OutputTypes = InOutType.FileInput.Concat(InOutType.ScalarInput).ToArray();
    private static readonly string[] AllInputTypes = InOutType.SourceInput.Concat(InOutType.ScalarInput).Concat(InOutType.FileInput).ToArray();
    private static readonly string[] NumericTypes = { InOutType.Integer, InOutType.Decimal };
    private static readonly string[] ChoiceTypes = { InOutType.String, InOutType.Integer, InOutType.Decimal };

    public static ScriptValidationResult Validate(string scriptDir)
    {
        var result = new ScriptValidationResult();
        var report = result.Report;

        // ---------------- Manifest ----------------
        string manifestPath = Path.Combine(scriptDir, MicsConstants.ScriptManifestFile);
        if (!File.Exists(manifestPath))
        {
            report.Error("manifest", $"{MicsConstants.ScriptManifestFile} not found.");
            return result;
        }

        ScriptManifest? manifest;
        try
        {
            var json = File.ReadAllText(manifestPath);
            var token = JToken.Parse(json);
            if (token is not JObject obj)
            {
                report.Error("manifest", "The manifest must be a JSON object.");
                return result;
            }
            manifest = obj.ToObject<ScriptManifest>();
        }
        catch (JsonReaderException ex)
        {
            report.Error("manifest", $"The manifest is not valid JSON: {ex.Message}");
            return result;
        }
        catch (Exception ex) when (ex is JsonSerializationException or FormatException or InvalidCastException or ArgumentException)
        {
            report.Error("manifest", $"A manifest value has the wrong type: {ex.Message}");
            return result;
        }

        if (manifest == null)
        {
            report.Error("manifest", "The manifest is empty.");
            return result;
        }

        manifest.Inputs ??= new();
        manifest.Outputs ??= new();
        manifest.OrchestrationInputs ??= new();
        manifest.OrchestrationOutputs ??= new();
        manifest.Localization ??= new();

        result.Manifest = manifest;
        report.ScriptId = manifest.ScriptId;
        report.Version = manifest.ScriptVersion;
        report.Label = manifest.ScriptLabel;
        report.EntryPoint = manifest.EntryPoint;

        // ---------------- Identity ----------------
        if (string.IsNullOrWhiteSpace(manifest.ScriptId))
            report.Error("manifest", "script_id is missing.");
        else if (!ScriptIdPattern.IsMatch(manifest.ScriptId))
            report.Error("manifest", $"script_id '{manifest.ScriptId}' may only contain letters, digits, '.', '_' and '-' (max 128).");

        if (string.IsNullOrWhiteSpace(manifest.ScriptVersion))
            report.Error("manifest", "version is missing.");
        else if (!VersionPattern.IsMatch(manifest.ScriptVersion))
            report.Error("manifest", $"version '{manifest.ScriptVersion}' may only contain letters, digits, '.', '_', '+' and '-' (max 64).");

        if (string.IsNullOrWhiteSpace(manifest.ScriptLabel))
            report.Error("manifest", "script_label is missing.");

        if (string.IsNullOrWhiteSpace(manifest.MinProcessorVersion))
            report.Warn("manifest", "processor_version is missing.");

        // ---------------- Entry point ----------------
        if (string.IsNullOrWhiteSpace(manifest.EntryPoint))
        {
            report.Error("manifest", "entry_point is missing.");
        }
        else if (!ScriptPathRules.TryResolveInside(scriptDir, manifest.EntryPoint, out var entryFull, out var entryError))
        {
            report.Error("manifest", $"entry_point '{manifest.EntryPoint}': {entryError}.");
        }
        else if (!File.Exists(entryFull))
        {
            report.Error("manifest", $"entry_point file not found: {manifest.EntryPoint}.");
        }
        else if (!manifest.EntryPoint.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            report.Warn("manifest", $"entry_point '{manifest.EntryPoint}' is not a .py file; the Python executor imports it as a module.");
        }

        // ---------------- Inputs ----------------
        var inputLabels = new Dictionary<string, InputMapping>(StringComparer.Ordinal);
        foreach (var input in manifest.Inputs)
        {
            string name = string.IsNullOrWhiteSpace(input.Label) ? "(no label)" : input.Label;

            if (string.IsNullOrWhiteSpace(input.Label))
                report.Error("inputs", "An input has no label.");
            else if (input.Label.StartsWith("__"))
                report.Error("inputs", $"Input label '{input.Label}' is reserved (labels starting with '__' are set by the executor).");
            else if (!inputLabels.TryAdd(input.Label, input))
                report.Error("inputs", $"Input label '{input.Label}' is used more than once.");

            if (string.IsNullOrWhiteSpace(input.Type) || !AllInputTypes.Contains(input.Type))
            {
                report.Error("inputs", $"Input '{name}' has unknown type '{input.Type}'. Allowed: {string.Join(", ", AllInputTypes)}.");
                continue;
            }

            if (InOutType.SourceInput.Contains(input.Type))
            {
                ValidateSourceInput(report, scriptDir, input, name);
            }
            else if (InOutType.FileInput.Contains(input.Type))
            {
                // Resolved under the job's working folder by the executor; must not escape it.
                if (!string.IsNullOrWhiteSpace(input.DiskPath) &&
                    !ScriptPathRules.TryNormalizeRelative(input.DiskPath, out _, out var err))
                    report.Error("inputs", $"Input '{name}' disk_path '{input.DiskPath}': {err}.");
            }
            else // scalar
            {
                if (!string.IsNullOrWhiteSpace(input.DiskPath))
                    report.Warn("inputs", $"Input '{name}' is a {input.Type}; its disk_path is ignored.");
            }

            ValidateFieldMetadata(report, input, name);
        }

        // ---------------- Outputs ----------------
        var outputLabels = new Dictionary<string, OutputMapping>(StringComparer.Ordinal);
        foreach (var output in manifest.Outputs)
        {
            string name = string.IsNullOrWhiteSpace(output.Label) ? "(no label)" : output.Label;

            if (string.IsNullOrWhiteSpace(output.Label))
                report.Error("outputs", "An output has no label.");
            else if (!outputLabels.TryAdd(output.Label, output))
                report.Error("outputs", $"Output label '{output.Label}' is used more than once.");

            if (string.IsNullOrWhiteSpace(output.Type) || !OutputTypes.Contains(output.Type))
            {
                report.Error("outputs", $"Output '{name}' has unknown type '{output.Type}'. Allowed: {string.Join(", ", OutputTypes)}.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(output.DiskPath) &&
                !ScriptPathRules.TryNormalizeRelative(output.DiskPath, out _, out var err))
                report.Error("outputs", $"Output '{name}' disk_path '{output.DiskPath}': {err}.");
        }

        // ---------------- Job form mapping ----------------
        var mappedInputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var map in manifest.OrchestrationInputs)
        {
            if (string.IsNullOrWhiteSpace(map.LabelParam))
            {
                report.Error("form", "An orchestrator_input_mapping entry has no label_param.");
                continue;
            }
            if (!inputLabels.TryGetValue(map.LabelParam, out var target))
            {
                report.Error("form", $"orchestrator_input_mapping '{map.LabelParam}' doesn't match any input label.");
                continue;
            }
            mappedInputs.Add(map.LabelParam);
            if (!string.Equals(map.Type, target.Type, StringComparison.Ordinal))
                report.Error("form", $"orchestrator_input_mapping '{map.LabelParam}' has type '{map.Type}' but the input is '{target.Type}'.");
            if (string.IsNullOrWhiteSpace(map.LocalizationLabel))
                report.Warn("form", $"orchestrator_input_mapping '{map.LabelParam}' has no text_label; the form will show the raw label.");
        }

        foreach (var input in manifest.Inputs.Where(i => !string.IsNullOrWhiteSpace(i.Label) && !InOutType.SourceInput.Contains(i.Type)))
        {
            if (mappedInputs.Contains(input.Label)) continue;
            bool required = !input.Optional && input.Default == null;
            if (required)
                report.Warn("form", $"Input '{input.Label}' is required but isn't in orchestrator_input_mapping, so the job form may not ask for it.");
            else
                report.Info("form", $"Input '{input.Label}' isn't in orchestrator_input_mapping and won't appear on the job form.");
        }

        foreach (var map in manifest.OrchestrationOutputs)
        {
            if (string.IsNullOrWhiteSpace(map.LabelParam))
            {
                report.Error("form", "An orchestrator_output_mapping entry has no label_param.");
                continue;
            }
            if (!outputLabels.TryGetValue(map.LabelParam, out var target))
            {
                report.Error("form", $"orchestrator_output_mapping '{map.LabelParam}' doesn't match any output label.");
                continue;
            }
            if (!string.Equals(map.Type, target.Type, StringComparison.Ordinal))
                report.Error("form", $"orchestrator_output_mapping '{map.LabelParam}' has type '{map.Type}' but the output is '{target.Type}'.");
            if (string.IsNullOrWhiteSpace(map.LocalizationLabel))
                report.Warn("form", $"orchestrator_output_mapping '{map.LabelParam}' has no text_label.");
        }

        // ---------------- Localization ----------------
        ValidateLocalization(report, scriptDir, manifest);

        // ---------------- Hash ----------------
        var hashed = ScriptHashing.ResolveHashedFiles(manifest, scriptDir);
        if (hashed.IsComplete)
        {
            report.ComputedHash = ScriptHashing.ComputeHash(scriptDir, hashed.Files);
            report.HashedFileCount = hashed.Files.Count;

            if (!string.IsNullOrWhiteSpace(manifest.ScriptHash))
            {
                if (ScriptHashing.HashEquals(manifest.ScriptHash, report.ComputedHash))
                {
                    report.Info("hash", "artifact_hash matches the files.");
                }
                else if (ScriptHashing.HashEquals(manifest.ScriptHash, ScriptHashing.ComputeLegacyHash(manifest, scriptDir)))
                {
                    report.Warn("hash", $"artifact_hash was made with the old method, which doesn't cover the entry point. Replace it with {report.ComputedHash}.");
                }
                else
                {
                    report.Error("hash", "artifact_hash doesn't match the files: the package was changed after it was published, or the hash is wrong.");
                }
            }
            else
            {
                report.Info("hash", "artifact_hash is empty; the computed hash will be recorded at install.");
            }
        }
        // Problems from ResolveHashedFiles were already reported as input / entry point errors.

        // ---------------- Size ----------------
        var allFiles = Directory.EnumerateFiles(scriptDir, "*", SearchOption.AllDirectories).ToList();
        report.FileCount = allFiles.Count;
        report.TotalBytes = allFiles.Sum(f => new FileInfo(f).Length);

        return result;
    }

    private static void ValidateSourceInput(ScriptValidationReport report, string scriptDir, InputMapping input, string name)
    {
        if (string.IsNullOrWhiteSpace(input.DiskPath))
        {
            report.Error("inputs", $"Input '{name}' ({input.Type}) needs a disk_path inside the package.");
            return;
        }

        if (!ScriptPathRules.TryResolveInside(scriptDir, input.DiskPath, out var full, out var err))
        {
            report.Error("inputs", $"Input '{name}' disk_path '{input.DiskPath}': {err}.");
            return;
        }

        if (input.Type == InOutType.SourceDir)
        {
            if (!Directory.Exists(full)) report.Error("inputs", $"Input '{name}': folder not found: {input.DiskPath}.");
            return;
        }

        if (!File.Exists(full))
        {
            report.Error("inputs", $"Input '{name}': file not found: {input.DiskPath}.");
            return;
        }

        if (input.Type == InOutType.ScriptFile && !input.DiskPath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            report.Warn("inputs", $"Input '{name}' is a script_file but '{input.DiskPath}' is not a .py file; the executor imports it as a module.");
    }

    private static void ValidateFieldMetadata(ScriptValidationReport report, InputMapping input, string name)
    {
        bool isNumeric = NumericTypes.Contains(input.Type);

        if (input.Choices != null && !ChoiceTypes.Contains(input.Type))
            report.Error("inputs", $"Input '{name}': 'choices' is only supported for string, integer and decimal inputs.");
        if ((input.Min.HasValue || input.Max.HasValue || input.Step.HasValue) && !isNumeric)
            report.Error("inputs", $"Input '{name}': min/max/step are only supported for integer and decimal inputs.");
        if (input.Default != null && !InOutType.ScalarInput.Contains(input.Type))
            report.Warn("inputs", $"Input '{name}': 'default' is only used for scalar inputs and is ignored for {input.Type}.");

        if (!InOutType.ScalarInput.Contains(input.Type)) return;

        if (input.Default != null && !TryParseScalar(input.Type, input.Default, out _))
            report.Error("inputs", $"Input '{name}': default '{input.Default}' is not a valid {input.Type}.");

        if (input.Type == InOutType.Boolean && input.Default != null &&
            TryParseScalar(input.Type, input.Default, out var b) && (bool)b!)
            report.Warn("inputs", $"Input '{name}': defaults to true. An unchecked checkbox may send no value, and an empty field gets the default, so users may not be able to turn it off. Booleans should normally default to false.");

        if (isNumeric)
        {
            if (input.Min.HasValue && input.Max.HasValue && input.Min > input.Max)
                report.Error("inputs", $"Input '{name}': min ({input.Min}) is greater than max ({input.Max}).");
            if (input.Step.HasValue && input.Step <= 0)
                report.Error("inputs", $"Input '{name}': step must be greater than 0.");
            if (input.Type == InOutType.Integer)
            {
                if (input.Min.HasValue && input.Min != decimal.Truncate(input.Min.Value))
                    report.Error("inputs", $"Input '{name}': min must be a whole number for an integer input.");
                if (input.Max.HasValue && input.Max != decimal.Truncate(input.Max.Value))
                    report.Error("inputs", $"Input '{name}': max must be a whole number for an integer input.");
            }

            if (input.Default != null && TryParseScalar(input.Type, input.Default, out var d))
            {
                decimal value = Convert.ToDecimal(d, CultureInfo.InvariantCulture);
                if ((input.Min.HasValue && value < input.Min) || (input.Max.HasValue && value > input.Max))
                    report.Error("inputs", $"Input '{name}': default {input.Default} is outside the range {input.Min?.ToString(CultureInfo.InvariantCulture) ?? "-∞"}..{input.Max?.ToString(CultureInfo.InvariantCulture) ?? "∞"}.");
            }
        }

        if (input.Choices != null)
        {
            if (input.Choices.Count == 0)
                report.Error("inputs", $"Input '{name}': 'choices' is empty.");

            foreach (var choice in input.Choices)
            {
                if (!TryParseScalar(input.Type, choice, out _))
                    report.Error("inputs", $"Input '{name}': choice '{choice}' is not a valid {input.Type}.");
            }

            if (input.Default != null && !input.Choices.Any(c => ScalarEquals(input.Type, c, input.Default)))
                report.Error("inputs", $"Input '{name}': default '{input.Default}' is not one of the choices.");
        }
    }

    private static void ValidateLocalization(ScriptValidationReport report, string scriptDir, ScriptManifest manifest)
    {
        if (manifest.Localization.Count == 0)
        {
            report.Warn("localization", "No localization files are listed; the UI will show raw labels.");
            return;
        }

        // Keys the UI looks up
        var expectedKeys = new List<string>();
        if (!string.IsNullOrWhiteSpace(manifest.ScriptLabel)) expectedKeys.Add(manifest.ScriptLabel);

        var inputsByLabel = manifest.Inputs
            .Where(i => !string.IsNullOrWhiteSpace(i.Label))
            .GroupBy(i => i.Label).ToDictionary(g => g.Key, g => g.First());

        foreach (var map in manifest.OrchestrationInputs.Where(m => !string.IsNullOrWhiteSpace(m.LocalizationLabel)))
        {
            expectedKeys.Add(map.LocalizationLabel);
            if (map.LabelParam != null && inputsByLabel.TryGetValue(map.LabelParam, out var input) && input.Choices != null)
                expectedKeys.AddRange(input.Choices.Select(c => $"{map.LocalizationLabel}.choice.{c}"));
        }
        expectedKeys.AddRange(manifest.OrchestrationOutputs
            .Where(m => !string.IsNullOrWhiteSpace(m.LocalizationLabel))
            .Select(m => m.LocalizationLabel));

        expectedKeys = expectedKeys.Distinct(StringComparer.Ordinal).ToList();

        string locDir = Path.Combine(scriptDir, MicsConstants.LocalizationDirectory);
        var checkedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!manifest.Localization.ContainsKey("en"))
            report.Info("localization", "There's no 'en' entry; the first listed file is used as the fallback.");

        foreach (var (culture, fileName) in manifest.Localization)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                report.Error("localization", $"Culture '{culture}' has no file name.");
                continue;
            }

            if (fileName.Contains('/') || fileName.Contains('\\') ||
                !ScriptPathRules.TryResolveInside(locDir, fileName, out var full, out var err))
            {
                report.Error("localization", $"Culture '{culture}': '{fileName}' must be a plain file name inside {MicsConstants.LocalizationDirectory}/.");
                continue;
            }

            if (!checkedFiles.Add(fileName)) continue; // several cultures can share one file

            if (!File.Exists(full))
            {
                report.Error("localization", $"Culture '{culture}': {MicsConstants.LocalizationDirectory}/{fileName} not found.");
                continue;
            }

            Dictionary<string, string>? entries;
            try
            {
                entries = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(full));
            }
            catch (JsonException ex)
            {
                report.Error("localization", $"{fileName} is not a flat JSON object of strings: {ex.Message}");
                continue;
            }

            entries ??= new();
            var missing = expectedKeys.Where(k => !entries.ContainsKey(k)).ToList();
            if (missing.Count > 0)
            {
                string list = string.Join(", ", missing.Take(8)) + (missing.Count > 8 ? $" and {missing.Count - 8} more" : "");
                report.Warn("localization", $"{fileName} is missing {missing.Count} key(s): {list}.");
            }
        }
    }

    private static bool TryParseScalar(string type, string raw, out object? value)
    {
        value = null;
        switch (type)
        {
            case InOutType.String:
                value = raw;
                return true;
            case InOutType.Integer:
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) { value = l; return true; }
                // JSON 5.0 arrives as "5" or "5.0" depending on the serializer; accept whole decimals.
                if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var whole) && whole == decimal.Truncate(whole))
                { value = (long)whole; return true; }
                return false;
            case InOutType.Decimal:
                if (decimal.TryParse(raw, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var d)) { value = d; return true; }
                return false;
            case InOutType.Boolean:
                if (bool.TryParse(raw, out var b)) { value = b; return true; }
                return false;
            default:
                return false;
        }
    }

    private static bool ScalarEquals(string type, string a, string b)
    {
        if (type == InOutType.String) return string.Equals(a, b, StringComparison.Ordinal);
        return TryParseScalar(type, a, out var va) && TryParseScalar(type, b, out var vb) &&
               Convert.ToDecimal(va, CultureInfo.InvariantCulture) == Convert.ToDecimal(vb, CultureInfo.InvariantCulture);
    }
}
