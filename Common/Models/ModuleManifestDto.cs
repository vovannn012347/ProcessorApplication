namespace Common.Models;

/// <summary>
/// Build-time-generated manifest for a module - OR for the host itself (ProcessorApplication),
/// which gets one the same way under its own output directory (see Build/ModuleManifest.targets,
/// imported by every module .csproj and by ProcessorApplication.csproj alike). This is what lets
/// ModuleLoader compute a dependency-respecting load order and check version compatibility purely
/// from JSON, without needing to load any assembly first - reading a module's real
/// IModule.Dependencies/Version requires loading it, which is exactly the catch-22 this breaks:
/// loading safely (without ReflectionTypeLoadException on a cross-module type) requires already
/// knowing the load order, but the real order lives on something you can't read without loading it.
///
/// ModuleId/Version here are a hand-declared duplicate of the module's real
/// IModule.ModuleId/Version in code (sourced from the module's own existing $(ModuleKey)/$(Version)
/// MSBuild properties, so nothing new needs declaring there) - ModuleLoader cross-checks both
/// against the real thing once a module actually loads and warns on drift, so staleness doesn't go
/// unnoticed.
/// </summary>
public class ModuleManifestDto
{
    public string ModuleId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public List<ModuleManifestDependencyDto> Dependencies { get; set; } = new();
}

/// <summary>One dependency entry within a ModuleManifestDto - mirrors Common.Models.ModuleDependency, but as plain strings (no System.Version) since this is parsed with zero assembly loading involved.</summary>
public class ModuleManifestDependencyDto
{
    public string ModuleId { get; set; } = string.Empty;
    public string? MinVersion { get; set; }
    public string? MaxVersion { get; set; }
}
