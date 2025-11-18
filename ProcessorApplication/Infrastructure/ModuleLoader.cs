using System.Reflection;

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

        foreach (var dir in Directory.GetDirectories(modulesPath))
        {
            var dll = Directory.GetFiles(dir, "*.dll")
                               .FirstOrDefault(f => Path.GetFileName(f).EndsWith("Module.dll"));
            if (dll == null) continue;

            var asm = Assembly.LoadFrom(dll);
            var type = asm.GetTypes()
                          .FirstOrDefault(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract);
            if (type == null) continue;

            var instance = (IModule)Activator.CreateInstance(type)!;
            list.Add(instance);
        }

        return list;
    }
}


//public static class ModuleLoader
//{
//    public static List<IModuleStartup> LoadModules(string path)
//    {
//        var modules = new List<IModuleStartup>();
//        if (!Directory.Exists(path))
//            return modules;

//        foreach (var dll in Directory.GetFiles(path, "*.dll", SearchOption.AllDirectories))
//        {
//            try
//            {
//                var asm = Assembly.LoadFrom(dll);
//                var types = asm.GetTypes()
//                    .Where(t => typeof(IModuleStartup).IsAssignableFrom(t) && !t.IsAbstract);

//                foreach (var t in types)
//                    if (Activator.CreateInstance(t) is IModuleStartup module)
//                        modules.Add(module);
//            }
//            catch { /* skip invalid DLLs */ }
//        }

//        return modules;
//    }

//    public static void ApplyConfigureAppConfiguration(
//        HostBuilderContext ctx,
//        IConfigurationBuilder builder,
//        IEnumerable<IModuleStartup> modules)
//    {
//        foreach (var module in modules)
//            module.ConfigureAppConfiguration(ctx, builder);
//    }

//    public static void ApplyConfigureServices(
//        HostBuilderContext ctx,
//        IServiceCollection services,
//        IEnumerable<IModuleStartup> modules)
//    {
//        foreach (var module in modules)
//            module.ConfigureServices(ctx, services);
//    }

//    public static void ApplyConfigureApp(
//        IApplicationBuilder app,
//        IEnumerable<IModuleStartup> modules)
//    {
//        foreach (var module in modules)
//            module.ConfigureApp(app);
//    }
//}