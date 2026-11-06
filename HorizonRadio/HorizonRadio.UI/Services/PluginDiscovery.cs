using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.UI.Services;

/// <summary>
/// Discovers plugins by scanning loaded assemblies for implementations of a plugin contract, rather
/// than the composition root naming each plugin type. This is the seam the install model rests on:
/// first-party plugins are referenced by the app (so they're present to discover), and third-party
/// plugins loaded later from a bundle get picked up by the exact same scan — the host never hardcodes
/// which plugins exist.
///
/// Assembly-scan order isn't deterministic, so <see cref="DiscoverPlugins{T}"/> orders results by
/// <see cref="IPlugin.SortOrder"/> (then <see cref="IPlugin.Id"/>) to keep the source picker and
/// metadata provider list stable.
/// </summary>
public static class PluginDiscovery
{
    /// <summary>
    /// Find every concrete, parameterless <typeparamref name="T"/> implementation across the app's
    /// plugin assemblies, instantiated and ordered by <see cref="IPlugin.SortOrder"/>.
    /// </summary>
    public static IReadOnlyList<T> DiscoverPlugins<T>() where T : class, IPlugin =>
        [.. Discover<T>().OrderBy(p => p.SortOrder).ThenBy(p => p.Id, StringComparer.Ordinal)];

    /// <summary>
    /// Find every concrete, public, parameterless <typeparamref name="T"/> implementation in the
    /// loaded HorizonRadio assemblies and instantiate it. Unordered — callers that need a stable
    /// order use <see cref="DiscoverPlugins{T}"/>.
    /// </summary>
    public static IReadOnlyList<T> Discover<T>() where T : class
    {
        EnsurePluginAssembliesLoaded();

        var results = new List<T>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            // Only our own assemblies can carry plugins; skip the framework/3rd-party set so a
            // GetTypes() over the whole load context stays cheap and can't throw on a foreign assembly.
            if (asm.GetName().Name is not { } name || !name.StartsWith("HorizonRadio.", StringComparison.Ordinal))
                continue;

            foreach (var type in SafeGetTypes(asm))
            {
                if (type is { IsClass: true, IsAbstract: false, IsPublic: true }
                    && typeof(T).IsAssignableFrom(type)
                    && type.GetConstructor(Type.EmptyTypes) is not null)
                {
                    results.Add((T)Activator.CreateInstance(type)!);
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
