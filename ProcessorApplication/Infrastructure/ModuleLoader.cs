using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

using Common.Models;

namespace ProcessorApplication.Infrastructure;

/// <summary>
/// The manifest a module's own .csproj copies to its output directory alongside its DLL (same
/// "Content, CopyToOutputDirectory" pattern already used for wwwroot files). This exists
/// specifically to break a catch-22: a module's real Dependencies live on its IModule
/// implementation, but reading that property requires the assembly to already be loaded - and
/// loading it safely (without ReflectionTypeLoadException on cross-module types) requires already
/// knowing the dependency order. The manifest is pure JSON, readable with zero assembly loading,
/// so ModuleLoader can compute the full load order BEFORE touching any DLL.
///
/// It is a hand-maintained duplicate of the Dependencies property, not a replacement for it -
/// ModuleLoader cross-checks the two once a module actually loads and logs a warning if they've
/// drifted apart, so staleness gets noticed rather than silently causing a wrong load order.
/// </summary>
public class ModuleManifestDto
{
    public string ModuleId { get; set; } = string.Empty;
    public List<ModuleManifestDependencyDto> Dependencies { get; set; } = new();
}

public class ModuleManifestDependencyDto
{
    public string ModuleId { get; set; } = string.Empty;
    public string? MinVersion { get; set; }
    public string? MaxVersion { get; set; }
}

public static class ModuleLoader
{
    private const string ManifestFileName = "module.manifest.json";

    /// <summary>
    /// The host process itself - never a loadable plugin module, always considered already
    /// satisfied. Modules declare a dependency on it (see P2PModuleEntryPoint etc.) to express
    /// "needs the host", not "needs another plugin module".
    /// </summary>
    private const string HostModuleId = "Main";

    public static IEnumerable<IModule> DiscoverModules(IServiceCollection hostServices, IConfiguration config, string basePath)
    {
        var modulesPath = Path.Combine(basePath, "Modules");
        var modulesList = new List<IModule>();

        if (!Directory.Exists(modulesPath)) return modulesList;

        // ---- Pass 1: find every module's DLL + manifest. Pure file I/O - no assembly loading,
        // so this works regardless of which module depends on which; nothing here needs anything
        // else to already be loaded.
        var candidates = new List<ModuleLoadCandidate>();

        foreach (var dir in Directory.GetDirectories(modulesPath))
        {
            var dllPath = Directory.GetFiles(dir, "*.dll")
                .FirstOrDefault(f => Path.GetFileName(f).EndsWith("Module.dll"));

            if (dllPath == null) continue;

            ModuleManifestDto? manifest = null;
            var manifestPath = Path.Combine(dir, ManifestFileName);

            if (File.Exists(manifestPath))
            {
                try
                {
                    manifest = JsonSerializer.Deserialize<ModuleManifestDto>(
                        File.ReadAllText(manifestPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ModuleLoader] Could not parse {ManifestFileName} in {dir}: {ex.Message}. This module will load with no known dependency order.");
                }
            }
            else
            {
                Console.WriteLine($"[ModuleLoader] No {ManifestFileName} in {dir} - this module will load with no known dependency order. Add one (ModuleId + Dependencies, matching the module's real IModule.Dependencies) if it depends on another plugin module.");
            }

            candidates.Add(new ModuleLoadCandidate(dir, dllPath, manifest));
        }

        // ---- Sort by manifest-declared dependencies BEFORE loading anything.
        var sortedCandidates = SortCandidatesByManifest(candidates);

        // ---- Pass 2: load for real, in that order. By the time a module's turn comes up,
        // everything it depends on (per its manifest) was already loaded in an earlier iteration
        // of this same loop.
        foreach (var candidate in sortedCandidates)
        {
            try
            {
                var alc = new ModuleAssemblyLoadContext(candidate.DllPath);
                ModuleRegistry.Contexts.Add(alc);

                var asm = alc.LoadFromAssemblyPath(candidate.DllPath);
                var viewsDllPath = candidate.DllPath.Replace(".dll", ".Views.dll");
                if (File.Exists(viewsDllPath)) alc.LoadFromAssemblyPath(viewsDllPath);

                var type = asm.GetTypes().FirstOrDefault(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract);
                if (type == null) continue;

                var instance = (IModule)Activator.CreateInstance(type)!;
                WarnIfManifestDrifted(candidate, instance);

                modulesList.Add(instance);
            }
            catch (ReflectionTypeLoadException ex)
            {
                foreach (var ex1 in ex.LoaderExceptions)
                {
                    Console.WriteLine(ex);

                    if (ex1 is FileNotFoundException fnf)
                    {
                        Console.WriteLine(fnf.FileName);
                        Console.WriteLine(fnf.FusionLog);
                    }
                }

                //foreach (var t in ex.Types)
                //    Console.WriteLine(t?.FullName ?? "<null>");

                foreach (var e in ex.LoaderExceptions)
                    Console.WriteLine(e);

                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModuleLoader] Failed to load {candidate.Dir}: {ex.Message}");
            }
        }

        return modulesList;
    }

    private record ModuleLoadCandidate(string Dir, string DllPath, ModuleManifestDto? Manifest);

    /// <summary>
    /// Logs a warning (never fails the load) if a module's actual ModuleId/Dependencies don't
    /// match what its manifest claimed - the manifest is hand-maintained, so this is how drift
    /// gets noticed instead of silently producing a wrong load order next time.
    /// </summary>
    private static void WarnIfManifestDrifted(ModuleLoadCandidate candidate, IModule instance)
    {
        if (candidate.Manifest == null) return;

        if (!string.Equals(candidate.Manifest.ModuleId, instance.ModuleId, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[ModuleLoader] {ManifestFileName} in {candidate.Dir} declares ModuleId '{candidate.Manifest.ModuleId}' but the loaded module reports '{instance.ModuleId}'. Fix the manifest - it drove this module's load-order position.");
        }

        var manifestDepIds = new HashSet<string>(candidate.Manifest.Dependencies.Select(d => d.ModuleId), StringComparer.OrdinalIgnoreCase);
        var actualDepIds = new HashSet<string>((instance.Dependencies ?? Array.Empty<ModuleDependency>()).Select(d => d.ModuleId), StringComparer.OrdinalIgnoreCase);

        if (!manifestDepIds.SetEquals(actualDepIds))
        {
            Console.WriteLine($"[ModuleLoader] {ManifestFileName} in {candidate.Dir} lists dependencies [{string.Join(", ", manifestDepIds)}] but the module's real Dependencies are [{string.Join(", ", actualDepIds)}]. Update the manifest to match, or this module may load in the wrong order next time.");
        }
    }

    /// <summary>
    /// Topological sort (DFS, visited/visiting sets for cycle detection) over not-yet-loaded
    /// candidates, keyed by manifest ModuleId. A candidate with no manifest (or no ModuleId in
    /// it) can't participate in the graph - it's appended wherever discovery order puts it, with
    /// the warning already logged above explaining why. HostModuleId ("Main") is always
    /// considered satisfied without needing to be found among candidates.
    /// </summary>
    private static List<ModuleLoadCandidate> SortCandidatesByManifest(List<ModuleLoadCandidate> candidates)
    {
        var byId = new Dictionary<string, ModuleLoadCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            var id = c.Manifest?.ModuleId;
            if (string.IsNullOrEmpty(id)) continue;

            if (!byId.TryAdd(id, c))
            {
                Console.WriteLine($"[ModuleLoader] Duplicate ModuleId '{id}' across manifests ({c.Dir} and {byId[id].Dir}) - keeping the first, ignoring the second for ordering.");
            }
        }

        var sorted = new List<ModuleLoadCandidate>(candidates.Count);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(ModuleLoadCandidate candidate, List<string> pathForCycleReporting)
        {
            var id = candidate.Manifest?.ModuleId;

            if (id != null)
            {
                if (visited.Contains(id)) return;

                if (!visiting.Add(id))
                {
                    Console.WriteLine($"[ModuleLoader] Dependency cycle detected: {string.Join(" -> ", pathForCycleReporting)} -> {id}. Breaking the cycle here; load order for this group is not guaranteed.");
                    return;
                }
            }

            foreach (var dep in candidate.Manifest?.Dependencies ?? new List<ModuleManifestDependencyDto>())
            {
                if (string.Equals(dep.ModuleId, HostModuleId, StringComparison.OrdinalIgnoreCase)) continue;

                if (!byId.TryGetValue(dep.ModuleId, out var depCandidate))
                {
                    Console.WriteLine($"[ModuleLoader] '{id}' depends on '{dep.ModuleId}' per its manifest, but no module with that id was found. It will load without that dependency satisfied.");
                    continue;
                }

                if (id != null) pathForCycleReporting.Add(id);
                Visit(depCandidate, pathForCycleReporting);
                if (id != null) pathForCycleReporting.RemoveAt(pathForCycleReporting.Count - 1);
            }

            if (id != null)
            {
                visiting.Remove(id);
                visited.Add(id);
            }

            sorted.Add(candidate);
        }

        foreach (var candidate in candidates)
        {
            Visit(candidate, new List<string>());
        }

        return sorted;
    }
}

public class ModuleAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _modulePath;
    private readonly string[] _additionalProbeDirectories;
    private readonly Dictionary<string, string>? _explicitAssemblyPaths;

    /// <summary>
    /// explicitAssemblyPaths: assembly simple name (no extension) -> exact file path, typically
    /// captured straight from MSBuild's own @(ReferencePath) at the referencing project's build
    /// (see Build/ModuleManifest.targets / Tools/ModuleManifestGenerator) - the same resolution
    /// the compiler itself just used, so when available it's checked before anything else here.
    /// Null for the real host, which doesn't need this (ModuleRegistry already tracks every other
    /// loaded module).
    ///
    /// additionalProbeDirectories: extra folders to check for a same-named DLL if nothing above
    /// knows about it - a folder-convention fallback for when no explicit path list was given.
    /// The real host never needs this either, for the same reason.
    /// </summary>
    public ModuleAssemblyLoadContext(
        string modulePath,
        IEnumerable<string>? additionalProbeDirectories = null,
        IReadOnlyDictionary<string, string>? explicitAssemblyPaths = null) : base(isCollectible: true)
    {
        _modulePath = modulePath;
        _resolver = new AssemblyDependencyResolver(modulePath);
        _additionalProbeDirectories = additionalProbeDirectories?.ToArray() ?? Array.Empty<string>();
        _explicitAssemblyPaths = explicitAssemblyPaths != null
            ? new Dictionary<string, string>(explicitAssemblyPaths, StringComparer.OrdinalIgnoreCase)
            : null;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 0. Has some OTHER module's ALC already loaded an assembly with this exact name? Reuse
        // that instance rather than loading a fresh copy - this is what keeps a shared dependency
        // (e.g. P2PModule, referenced by FileSharingModule) to a single CLR type identity across
        // every module that uses it, instead of each module's ALC loading its own separate copy
        // (which would make e.g. FileSharingModule's "IPeerTracker" a DIFFERENT type than
        // P2PModule's own "IPeerTracker", breaking DI resolution and casts between them).
        var alreadyLoaded = ModuleRegistry.ResolveFromAnyModule(assemblyName);
        if (alreadyLoaded != null)
        {
            return alreadyLoaded;
        }

        // 1. An exact, MSBuild-resolved path for this assembly name (see constructor remarks) -
        // the most authoritative source when available, since it's not a guess.
        if (_explicitAssemblyPaths != null &&
            assemblyName.Name != null &&
            _explicitAssemblyPaths.TryGetValue(assemblyName.Name, out var explicitPath) &&
            File.Exists(explicitPath))
        {
            return LoadFromAssemblyPath(explicitPath);
        }

        string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);

        if (assemblyPath != null)
        {
            // If the map points to a path, we load it. 
            // If the map points to a framework assembly, the resolver 
            // usually returns null (delegating to the default context).
            return LoadFromAssemblyPath(assemblyPath);
        }

        // 3. Extra probe directories (see constructor remarks) - folder-convention fallback for
        // when no explicit path list was given at all.
        foreach (var dir in _additionalProbeDirectories)
        {
            var candidate = Path.Combine(dir, assemblyName.Name + ".dll");
            if (File.Exists(candidate))
            {
                return LoadFromAssemblyPath(candidate);
            }
        }

        // 4. Fallback: If nothing above knows, it's likely a shared host assembly.
        // Returning null here tells the runtime: "Check the Default Load Context."
        // This is where the magic happens: the Host's context finds it immediately.
        return base.Load(assemblyName);
    }
}

public static class ModuleRegistry
{
    public static readonly List<ModuleAssemblyLoadContext> Contexts = new();

    public static Assembly? ResolveFromAnyModule(AssemblyName name)
    {
        foreach (var context in Contexts)
        {
            // Check if this context has already loaded the requested assembly
            var loaded = context.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
            if (loaded != null) return loaded;
        }
        return null;
    }

    public static readonly ConcurrentDictionary<string, Assembly> DefaultContexts = new();
    public static readonly ConcurrentDictionary<string, Assembly> DomainContexts = new();

    // ASP.NET Core/CLR may perform reflection from the Default AssemblyLoadContext.
    // Plugin assemblies live in isolated collectible ALCs, so Default cannot resolve
    // them by name. This registry bridges that gap by returning already-loaded plugin
    // assemblies instead of letting the runtime fail with FileNotFoundException.
    public static void InstallResolutionDivert()
    {
        // crutch for assembly resolutions
        // really whatever i tired - it is still has to be in here :(
        // Hook 1: Satisfies the modern AssemblyLoadContext metadata tracking engine
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            if(DefaultContexts.TryGetValue(assemblyName.FullName, out var assembly)){
                return assembly;
            }
            else
            {
                foreach (var alc in ProcessorApplication.Infrastructure.ModuleRegistry.Contexts)
                {
                    var match = alc.Assemblies.FirstOrDefault(a => a.GetName().FullName == assemblyName.FullName);
                    if (match != null)
                    {
                        DefaultContexts[assemblyName.FullName] = match;
                        return match;
                    }
                }
            }

            return null;
        };
        // Hook 2: Satisfies the legacy AppDomain reflection engine used by deep CLR calls like _CreateCaObject
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            var reqName = new System.Reflection.AssemblyName(args.Name).Name;
            if (DomainContexts.TryGetValue(reqName, out var assembly))
            {
                return assembly;
            }
            else
            {
                foreach (var alc in ProcessorApplication.Infrastructure.ModuleRegistry.Contexts)
                {
                    var match = alc.Assemblies.FirstOrDefault(a => a.GetName().Name == reqName);
                    if (match != null)
                    {
                        DomainContexts[reqName] = match;
                        return match;
                    }
                }
            }
            return null;
        };
    }
}
