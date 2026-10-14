using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

using Common.Models;

namespace ProcessorApplication.Infrastructure;

public static class ModuleLoader
{
    /// <summary>
    /// Each participant's manifest is named "{ModuleKey}.manifest.json" (see
    /// Build/ModuleManifest.targets for why - a fixed shared name collides with
    /// /Modules/Directory.Build.targets' own duplicate-stripping, which skips any file whose
    /// relative-path+filename already exists under the host's output root; the host gets a
    /// manifest the exact same way every module does, so a fixed name meant every module's own
    /// manifest got dropped as a false "duplicate" of the host's). The exact filename is therefore
    /// not knowable in advance - it's module-key-dependent, which is exactly what reading the file
    /// is for - so this is a search pattern, not a fixed name.
    /// </summary>
    private const string ManifestFileSearchPattern = "*.manifest.json";

    /// <summary>
    /// Finds the one file matching ManifestFileSearchPattern in a directory. Null if there isn't
    /// one. More than one (most likely a stale leftover from a renamed ModuleKey, since the old
    /// name never gets cleaned up by a build that now writes a different one) is not an error -
    /// the most recently written one wins, with a warning identifying the rest so they can be
    /// deleted, rather than silently picking whichever GetFiles happens to return first.
    /// </summary>
    private static string? FindManifestFile(string dir)
    {
        var matches = Directory.GetFiles(dir, ManifestFileSearchPattern);
        if (matches.Length == 0) return null;
        if (matches.Length == 1) return matches[0];

        var newest = matches.OrderByDescending(File.GetLastWriteTimeUtc).First();
        var stale = matches.Where(m => m != newest);
        Console.WriteLine($"[ModuleLoader] Found {matches.Length} files matching {ManifestFileSearchPattern} in {dir} - using the most recently written one ({Path.GetFileName(newest)}). Delete the stale one(s) ({string.Join(", ", stale.Select(Path.GetFileName))}), likely left over from a renamed ModuleKey.");
        return newest;
    }

    public static IEnumerable<IModule> DiscoverModules(IServiceCollection hostServices, IConfiguration config, string basePath)
    {
        var modulesList = new List<IModule>();
        // ModuleId -> real, known version. Seeded with the host's own (from its manifest, read
        // below) and grown as each plugin module actually loads - this is what a dependency's
        // MinVersion/MaxVersion gets checked against. A module's SELF-declared manifest Version
        // never goes in here; only a confirmed, actually-resolved version does.
        var knownVersions = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<ModuleLoadCandidate>();

        // ---- The host itself. Read as a root entry in the very same candidates/sort graph as
        // every plugin module - no more hardcoded "Main is always satisfied" special case anywhere
        // below. ProcessorApplication.csproj imports the same Build/ModuleManifest.targets as
        // every module, so this manifest exists at its own output root the same way. DllPath stays
        // null: the host is already running, it's never "loaded" the way a plugin module is - its
        // only purpose here is to give dependents something real to resolve against (a load-order
        // placeholder, and a real Version to check).
        var hostManifestPath = FindManifestFile(basePath);
        if (hostManifestPath != null)
        {
            try
            {
                var hostManifest = JsonSerializer.Deserialize<ModuleManifestDto>(
                    File.ReadAllText(hostManifestPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (hostManifest != null && !string.IsNullOrEmpty(hostManifest.ModuleId))
                {
                    candidates.Add(new ModuleLoadCandidate(basePath, null, hostManifest));

                    if (Version.TryParse(hostManifest.Version, out var hostVersion))
                    {
                        knownVersions[hostManifest.ModuleId] = hostVersion;
                        Console.WriteLine($"[ModuleLoader] Host '{hostManifest.ModuleId}' v{hostVersion} is up. Going module hunting in {Path.Combine(basePath, "Modules")}...");
                    }
                    else
                    {
                        Console.WriteLine($"[ModuleLoader] Host manifest ({Path.GetFileName(hostManifestPath)}) has an unparseable Version '{hostManifest.Version}' - dependents can't have their version requirement checked against it.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModuleLoader] Could not parse the host's own manifest at {hostManifestPath}: {ex.Message}. Modules depending on it will load without that dependency resolved.");
            }
        }
        else
        {
            Console.WriteLine($"[ModuleLoader] No {ManifestFileSearchPattern} found for the host itself in {basePath} - add Build/ModuleManifest.targets support to ProcessorApplication.csproj if you haven't. Modules depending on it will load without that dependency resolved.");
        }

        var modulesPath = Path.Combine(basePath, "Modules");
        if (!Directory.Exists(modulesPath))
        {
            Console.WriteLine($"[ModuleLoader] No Modules folder at {modulesPath} - running host-only. Quiet day.");
            return modulesList;
        }

        // ---- Pass 1: find every module's DLL + manifest. Pure file I/O - no assembly loading,
        // so this works regardless of which module depends on which; nothing here needs anything
        // else to already be loaded.
        foreach (var dir in Directory.GetDirectories(modulesPath))
        {
            var dllPath = Directory.GetFiles(dir, "*.dll")
                .FirstOrDefault(f => Path.GetFileName(f).EndsWith("Module.dll"));

            if (dllPath == null) continue;

            ModuleManifestDto? manifest = null;
            var manifestPath = FindManifestFile(dir);

            if (manifestPath != null)
            {
                try
                {
                    manifest = JsonSerializer.Deserialize<ModuleManifestDto>(
                        File.ReadAllText(manifestPath),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ModuleLoader] Could not parse manifest ({Path.GetFileName(manifestPath)}) in {dir}: {ex.Message}. This module will load with no known dependency order.");
                }
            }
            else
            {
                Console.WriteLine($"[ModuleLoader] No {ManifestFileSearchPattern} in {dir} - this module will load with no known dependency order. Add one (ModuleId + Version + Dependencies, matching the module's real IModule properties) if it depends on another plugin module.");
            }

            candidates.Add(new ModuleLoadCandidate(dir, dllPath, manifest));
        }

        // ---- Sort by manifest-declared dependencies BEFORE loading anything. The host entry
        // added above participates in exactly the same graph.
        var sortedCandidates = SortCandidatesByManifest(candidates);

        int pluginCount = sortedCandidates.Count(c => c.DllPath != null);
        Console.WriteLine($"[ModuleLoader] Found {pluginCount} module(s). Load order: {string.Join(" -> ", sortedCandidates.Where(c => c.DllPath != null).Select(DescribeCandidate))}");

        int failed = 0;
        var totalTimer = System.Diagnostics.Stopwatch.StartNew();

        // ---- Pass 2: load for real, in that order. By the time a module's turn comes up,
        // everything it depends on (per its manifest) was already loaded in an earlier iteration
        // of this same loop - or, for the host, was already running from the very start.
        foreach (var candidate in sortedCandidates)
        {
            if (candidate.DllPath == null) continue; // the host's own root entry - nothing to load

            var loadTimer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var alc = new ModuleAssemblyLoadContext(candidate.DllPath);
                ModuleRegistry.Contexts.Add(alc);

                var asm = alc.LoadFromAssemblyPath(candidate.DllPath);
                var viewsDllPath = candidate.DllPath.Replace(".dll", ".Views.dll");
                if (File.Exists(viewsDllPath)) alc.LoadFromAssemblyPath(viewsDllPath);

                var type = asm.GetTypes().FirstOrDefault(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract);
                if (type == null)
                {
                    Console.WriteLine($"[ModuleLoader] {Path.GetFileName(candidate.DllPath)} has no IModule implementation - nothing to wake up there, skipping.");
                    continue;
                }

                var instance = (IModule)Activator.CreateInstance(type)!;
                WarnIfManifestDrifted(candidate, instance);

                // Check this module's real dependency requirements against whatever's actually
                // known so far (the host, plus every module already loaded earlier in this same
                // sorted pass) BEFORE registering this module's own version - a module never
                // needs to check itself.
                var depReport = CheckDependencyVersions(instance, knownVersions);

                knownVersions[instance.ModuleId] = instance.Version ?? new Version(0, 0, 0, 0);
                modulesList.Add(instance);

                loadTimer.Stop();
                string deps = depReport.Count == 0
                    ? "no dependencies, a free spirit"
                    : "dependencies: " + string.Join(", ", depReport);
                Console.WriteLine($"[ModuleLoader] Loaded '{instance.ModuleId}' ({instance.Name}) v{instance.Version} in {loadTimer.ElapsedMilliseconds} ms - {deps}");
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
                failed++;
                Console.WriteLine($"[ModuleLoader] Failed to load {candidate.Dir}: {ex.Message}");
            }
        }

        totalTimer.Stop();
        Console.WriteLine(failed == 0
            ? $"[ModuleLoader] All aboard: {modulesList.Count} module(s) loaded in {totalTimer.ElapsedMilliseconds} ms - {string.Join(", ", modulesList.Select(m => $"{m.ModuleId} v{m.Version}"))}."
            : $"[ModuleLoader] {modulesList.Count} module(s) loaded, {failed} failed (see above) in {totalTimer.ElapsedMilliseconds} ms - {string.Join(", ", modulesList.Select(m => $"{m.ModuleId} v{m.Version}"))}.");

        return modulesList;
    }

    private static string DescribeCandidate(ModuleLoadCandidate c) =>
        c.Manifest?.ModuleId is { Length: > 0 } id
            ? $"{id} v{c.Manifest.Version}"
            : Path.GetFileNameWithoutExtension(c.DllPath ?? c.Dir) + " (no manifest)";

    private record ModuleLoadCandidate(string Dir, string? DllPath, ModuleManifestDto? Manifest);

    /// <summary>
    /// Logs a warning (never fails the load) if a module's actual ModuleId/Version/Dependencies
    /// don't match what its manifest claimed - the manifest is hand-maintained (sourced from the
    /// module's own $(ModuleKey)/$(Version)/ModuleManifestDependency items), so this is how drift
    /// gets noticed instead of silently producing a wrong load order or an unchecked version next
    /// time.
    /// </summary>
    private static void WarnIfManifestDrifted(ModuleLoadCandidate candidate, IModule instance)
    {
        if (candidate.Manifest == null) return;

        if (!string.Equals(candidate.Manifest.ModuleId, instance.ModuleId, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[ModuleLoader] Manifest in {candidate.Dir} declares ModuleId '{candidate.Manifest.ModuleId}' but the loaded module reports '{instance.ModuleId}'. Fix the manifest - it drove this module's load-order position.");
        }

        if (Version.TryParse(candidate.Manifest.Version, out var manifestVersion))
        {
            if (instance.Version != null && manifestVersion != instance.Version)
            {
                Console.WriteLine($"[ModuleLoader] Manifest in {candidate.Dir} declares Version '{candidate.Manifest.Version}' but the loaded module reports '{instance.Version}'. Fix the module's $(Version) in its .csproj - other modules' version requirements were checked against the stale manifest value.");
            }
        }
        else if (!string.IsNullOrEmpty(candidate.Manifest.Version))
        {
            Console.WriteLine($"[ModuleLoader] Manifest in {candidate.Dir} has an unparseable Version '{candidate.Manifest.Version}'.");
        }

        var manifestDepIds = new HashSet<string>(candidate.Manifest.Dependencies.Select(d => d.ModuleId), StringComparer.OrdinalIgnoreCase);
        var actualDepIds = new HashSet<string>((instance.Dependencies ?? Array.Empty<ModuleDependency>()).Select(d => d.ModuleId), StringComparer.OrdinalIgnoreCase);

        if (!manifestDepIds.SetEquals(actualDepIds))
        {
            Console.WriteLine($"[ModuleLoader] Manifest in {candidate.Dir} lists dependencies [{string.Join(", ", manifestDepIds)}] but the module's real Dependencies are [{string.Join(", ", actualDepIds)}]. Update the manifest to match, or this module may load in the wrong order next time.");
        }
    }

    /// <summary>
    /// Compares this module's real Dependencies (the actual IModule.Dependencies property - the
    /// manifest only ever drives load ORDER before anything is loaded; once a module IS loaded,
    /// its own Dependencies plus whatever's actually known so far are authoritative for everything
    /// else) against the real version of whichever dependency is already known - the host (seeded
    /// up front from its own manifest) or an already-loaded module (added to knownVersions right
    /// after it loads). The topological sort guarantees a dependency is already known by the time
    /// whatever depends on it is checked.
    ///
    /// A mismatch is logged, never fatal - matching this loader's existing catch-and-continue
    /// style throughout: warn, then load anyway regardless of outcome.
    /// </summary>
    /// <returns>One short line per dependency for the "Loaded ..." log line, e.g. "P2P v1.0.0.0 (wants >= 1.0.0.0) ok".</returns>
    private static List<string> CheckDependencyVersions(IModule module, Dictionary<string, Version> knownVersions)
    {
        var report = new List<string>();

        foreach (var dep in module.Dependencies ?? Array.Empty<ModuleDependency>())
        {
            string wants = (dep.MinVersion, dep.MaxVersion) switch
            {
                (not null, not null) => $"wants {dep.MinVersion}..{dep.MaxVersion}",
                (not null, null) => $"wants >= {dep.MinVersion}",
                (null, not null) => $"wants <= {dep.MaxVersion}",
                _ => "any version"
            };

            if (!knownVersions.TryGetValue(dep.ModuleId, out var actualVersion))
            {
                // Not found/not loaded - already warned about elsewhere (missing manifest entry,
                // sort couldn't resolve it, etc.). Nothing new to say, just nothing to compare.
                report.Add($"{dep.ModuleId} ({wants}) MISSING");
                continue;
            }

            bool ok = true;
            if (dep.MinVersion != null && actualVersion < dep.MinVersion)
            {
                ok = false;
                Console.WriteLine($"[ModuleLoader] '{module.ModuleId}' requires '{dep.ModuleId}' >= {dep.MinVersion}, found {actualVersion}. Loading anyway.");
            }
            if (dep.MaxVersion != null && actualVersion > dep.MaxVersion)
            {
                ok = false;
                Console.WriteLine($"[ModuleLoader] '{module.ModuleId}' requires '{dep.ModuleId}' <= {dep.MaxVersion}, found {actualVersion}. Loading anyway.");
            }

            report.Add($"{dep.ModuleId} v{actualVersion} ({wants}) {(ok ? "ok" : "VERSION MISMATCH")}");
        }

        return report;
    }

    /// <summary>
    /// Topological sort (DFS, visited/visiting sets for cycle detection) over not-yet-loaded
    /// candidates, keyed by manifest ModuleId - this includes the host's own root entry, resolved
    /// through the exact same lookup as any plugin module dependency, no special-casing needed. A
    /// candidate with no manifest (or no ModuleId in it) can't participate in the graph - it's
    /// appended wherever discovery order puts it, with the warning already logged above explaining
    /// why.
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
    /// (see Build/ModuleManifest.targets) - the same resolution the compiler itself just used, so
    /// when available it's checked before anything else here. Null for the real host, which
    /// doesn't need this (ModuleRegistry already tracks every other loaded module).
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
        // (e.g. P2PModule, referenced by FileTransferModule) to a single CLR type identity across
        // every module that uses it, instead of each module's ALC loading its own separate copy
        // (which would make e.g. FileTransferModule's "IPeerTracker" a DIFFERENT type than
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
