using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HorizonRadio.Core.Hosting;
using HorizonRadio.Sdk;

namespace HorizonRadio.UI.Services;

/// <summary>
/// Finds the plugin types in the app's plugin assemblies, rather than the composition root naming
/// each plugin. This is the seam the install model rests on: first-party plugins are referenced by
/// the app (so they're present to scan), and third-party plugins loaded later from a bundle get
/// picked up by the same scan — the host never hardcodes which plugins exist.
///
/// It only finds types; the container builds them (<see cref="PluginRegistration.AddPlugins"/>),
/// so a plugin's constructor can take host services, and the order plugins are shown in comes from
/// <see cref="IPlugin.SortOrder"/> (<see cref="PluginRegistration.InDisplayOrder{T}"/>).
/// </summary>
public static class PluginDiscovery
{
    /// <summary>Every public, concrete class in the loaded HorizonRadio assemblies that is a plugin
    /// or a plugin module.</summary>
    public static IReadOnlyList<Type> PluginTypes()
    {
        EnsurePluginAssembliesLoaded();

        var results = new List<Type>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            // Only our own assemblies can carry plugins; skip the framework/3rd-party set so a
            // GetTypes() over the whole load context stays cheap and can't throw on a foreign assembly.
            if (asm.GetName().Name is not { } name || !name.StartsWith("HorizonRadio.", StringComparison.Ordinal))
                continue;

            foreach (var type in SafeGetTypes(asm))
            {
                if (type is { IsClass: true, IsAbstract: false, IsPublic: true }
                    && (typeof(IPluginModule).IsAssignableFrom(type)
                        || PluginRegistration.Kinds.Any(k => k.IsAssignableFrom(type))))
                {
                    results.Add(type);
                }
            }
        }
        return results;
    }

    // Once the composition root stops naming plugin types, the compiler drops the IL reference to
    // those assemblies and the runtime won't have loaded them when we scan. We can't enumerate them
    // dynamically in a single-file app — DependencyContext.Default is null there and there's no API to
    // list bundle contents — so the build emits one [AssemblyMetadata("PluginAssembly_X","X")] per
    // referenced HorizonRadio assembly (see the .csproj). Load each by name up front; that resolves
    // from the bundle in single-file and from disk in a normal build. Loading a loaded assembly no-ops.
    private static void EnsurePluginAssembliesLoaded()
    {
        var entry = Assembly.GetEntryAssembly();
        if (entry is null) return;

        foreach (var attr in entry.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (!attr.Key.StartsWith("PluginAssembly_", StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(attr.Value)) continue;
            try { Assembly.Load(new AssemblyName(attr.Value)); }
            catch { /* not loadable by simple name (resource/native) — nothing to scan there */ }
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }
}
