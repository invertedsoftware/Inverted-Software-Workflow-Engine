// Copyright (c) Inverted Software. All rights reserved.

using System.Collections.Concurrent;
using System.Reflection;

namespace InvertedSoftware.WorkflowEngine.Queue;

/// <summary>
/// Resolves CLR <see cref="Type"/> instances from the type-name strings carried in
/// <see cref="MessageHeaders.MessageType"/>. Caches every lookup so the
/// <c>AppDomain.GetAssemblies()</c> scan only happens once per distinct name —
/// avoiding per-message reflection cost on the hot path.
/// </summary>
public static class TypeNameResolver
{
    private static readonly ConcurrentDictionary<string, Type?> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolve <paramref name="typeName"/> to a <see cref="Type"/>.
    /// </summary>
    /// <param name="typeName">An assembly-qualified or simple full name (e.g. "MyApp.MyMessage" or
    /// "MyApp.MyMessage, MyApp").</param>
    /// <returns>The resolved type, or <c>null</c> if not found in any loaded assembly.</returns>
    public static Type? Resolve(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;
        return Cache.GetOrAdd(typeName, ResolveCore);
    }

    /// <summary>
    /// Resolve <paramref name="typeName"/> to a <see cref="Type"/>, throwing
    /// <see cref="MessageDeserializationException"/> when not found.
    /// </summary>
    public static Type ResolveOrThrow(string typeName)
    {
        var t = Resolve(typeName)
            ?? throw new MessageDeserializationException(
                $"Type '{typeName}' could not be resolved from any loaded assembly. " +
                $"Verify the message-class assembly is referenced by the consumer.");
        return t;
    }

    private static Type? ResolveCore(string typeName)
    {
        // Fast path: assembly-qualified name.
        var t = Type.GetType(typeName, throwOnError: false);
        if (t is not null) return t;

        // Fallback: scan every loaded assembly. Costly the first time per type name;
        // subsequent calls hit the dictionary cache.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            t = asm.GetType(typeName, throwOnError: false);
            if (t is not null) return t;
        }

        // Last resort: load assemblies from the same directory as the entry assembly
        // and try again. Covers plugin scenarios where the message class lives in a
        // sibling DLL that hasn't been touched by the consumer yet.
        var entry = Assembly.GetEntryAssembly();
        if (entry is not null)
        {
            var dir = Path.GetDirectoryName(entry.Location);
            if (!string.IsNullOrEmpty(dir))
            {
                foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
                {
                    try
                    {
                        var asm = Assembly.LoadFrom(dll);
                        t = asm.GetType(typeName, throwOnError: false);
                        if (t is not null) return t;
                    }
                    catch
                    {
                        // Skip unloadable / mixed-mode / native DLLs silently.
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Clears the cache. Test-only.</summary>
    internal static void ClearCacheForTests() => Cache.Clear();
}
