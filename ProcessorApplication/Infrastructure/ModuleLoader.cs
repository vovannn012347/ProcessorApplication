using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace ProcessorApplication.Infrastructure;
public static class ModuleLoader
{
    public static IEnumerable<IModule> DiscoverModules(IServiceCollection hostServices, IConfiguration config, string basePath)
    {
        var modulesPath = Path.Combine(basePath, "Modules");
        var modulesList = new List<IModule>();

        if (!Directory.Exists(modulesPath)) return modulesList;

        foreach (var dir in Directory.GetDirectories(modulesPath))
        {
            var dllPath = Directory.GetFiles(dir, "*.dll")
                .FirstOrDefault(f => Path.GetFileName(f).EndsWith("Module.dll"));

            if (dllPath == null) continue;

            try
            {
                var alc = new ModuleAssemblyLoadContext(dllPath);
                ModuleRegistry.Contexts.Add(alc);

                var asm = alc.LoadFromAssemblyPath(dllPath);
                var viewsDllPath = dllPath.Replace(".dll", ".Views.dll");
                if (File.Exists(viewsDllPath)) alc.LoadFromAssemblyPath(viewsDllPath);

                var type = asm.GetTypes().FirstOrDefault(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract);
                if (type == null) continue;

                var instance = (IModule)Activator.CreateInstance(type)!;
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
                Console.WriteLine($"[ModuleLoader] Failed to load {dir}: {ex.Message}");
            }
        }

        return modulesList;
    }
}

public class ModuleAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _modulePath;

    public ModuleAssemblyLoadContext(string modulePath) : base(isCollectible: true)
    {
        _modulePath = modulePath;
        _resolver = new AssemblyDependencyResolver(modulePath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);

        if (assemblyPath != null)
        {
            // If the map points to a path, we load it. 
            // If the map points to a framework assembly, the resolver 
            // usually returns null (delegating to the default context).
            return LoadFromAssemblyPath(assemblyPath);
        }

        // 2. Fallback: If resolver doesn't know, it's likely a shared host assembly.
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
