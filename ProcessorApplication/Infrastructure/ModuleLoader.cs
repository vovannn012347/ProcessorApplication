using System.Reflection;
using System.Runtime.Loader;

using Common;
using Common.Models;

namespace ProcessorApplication.Infrastructure;
public static class ModuleLoader
{
    public static IEnumerable<IModule> DiscoverModules(string basePath)
    {
        var modulesPath = Path.Combine(basePath, "Modules");
        var list = new List<IModule>();

        if (!Directory.Exists(modulesPath)) return list;

        // Process each directory in the Modules folder
        foreach (var dir in Directory.GetDirectories(modulesPath))
        {
            var dllPath = Directory.GetFiles(dir, "*.dll")
                .FirstOrDefault(f => Path.GetFileName(f).EndsWith("Module.dll"));

            if (dllPath == null) continue;

            try
            {
                var alc = new ModuleAssemblyLoadContext(dllPath);

                // Add to registry so subsequent modules can resolve shared dependencies
                ModuleRegistry.Contexts.Add(alc);

                var asm = alc.LoadFromAssemblyPath(dllPath);
                var viewsDllPath = dllPath.Replace(".dll", ".Views.dll");
                if (File.Exists(viewsDllPath))
                {
                    // Load the views assembly into the SAME context
                    alc.LoadFromAssemblyPath(viewsDllPath);
                }

                // Find the class that implements IModule
                var type = asm.GetTypes()
                    .FirstOrDefault(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract);

                if (type == null) continue;

                var instance = (IModule)Activator.CreateInstance(type)!;
                list.Add(instance);
            }
            catch (Exception ex)
            {
                // In a modular system, one failing module shouldn't crash the host
                Console.WriteLine($"[ModuleLoader] Failed to load {dir}: {ex.Message}");
            }
        }

        return list;
    }
}

public class ModuleAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public ModuleAssemblyLoadContext(string modulePath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(modulePath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 1. HOST-FIRST: If the Host already has this assembly (System, Microsoft, or Main Project),
        // we MUST return null to force the module to use the Host's version.
        try
        {
            // This checks the Default context without loading the assembly if it's not there
            var hostAssembly = Default.LoadFromAssemblyName(assemblyName);
            if (hostAssembly != null) return null;
        }
        catch { /* Not found in Host, continue to module resolution */ }

        // 2. PEER-RESOLUTION: Check if another module already loaded this dependency
        var sharedAsm = ModuleRegistry.ResolveFromAnyModule(assemblyName);
        if (sharedAsm != null) return sharedAsm;

        // 3. LOCAL-RESOLUTION: Try to find it in the module's own folder via .deps.json
        string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath != null)
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        return null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (libraryPath != null) return LoadUnmanagedDllFromPath(libraryPath);
        return IntPtr.Zero;
    }
}

public static class ModuleRegistry
{
    // Keeps track of all loaded module contexts
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
}