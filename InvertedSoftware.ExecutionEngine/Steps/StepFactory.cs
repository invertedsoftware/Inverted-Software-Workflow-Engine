// Copyright (c) Inverted Software. All rights reserved.

using System.Collections.Concurrent;
using InvertedSoftware.WorkflowEngine.Common.Exceptions;
using InvertedSoftware.WorkflowEngine.Queue;

namespace InvertedSoftware.WorkflowEngine.Steps;

/// <summary>
/// Default <see cref="IStepFactory"/>. Resolves by name in this priority order:
/// <list type="number">
///   <item>An explicit registration in the in-memory factory dictionary.</item>
///   <item>A type discovered via <see cref="TypeNameResolver"/> (assembly-qualified
///         or simple full name; results are cached).</item>
/// </list>
/// </summary>
public sealed class TypeNameStepFactory : IStepFactory
{
    private readonly Dictionary<string, Func<IStep>> _registry;

    // Activator cache for runtime-resolved types so we only reflect once per name.
    private readonly ConcurrentDictionary<string, Func<IStep>> _resolvedCache = new(StringComparer.Ordinal);

    public TypeNameStepFactory()
        : this(new Dictionary<string, Func<IStep>>(StringComparer.Ordinal)) { }

    public TypeNameStepFactory(IDictionary<string, Func<IStep>> registry)
    {
        _registry = new Dictionary<string, Func<IStep>>(registry, StringComparer.Ordinal);
    }

    /// <summary>Register a factory for the given type name. Replaces any prior registration.</summary>
    public TypeNameStepFactory Register<TStep>(string invokeClassName, Func<TStep> factory)
        where TStep : IStep
    {
        ArgumentException.ThrowIfNullOrEmpty(invokeClassName);
        ArgumentNullException.ThrowIfNull(factory);
        _registry[invokeClassName] = () => factory();
        return this;
    }

    public IStep GetStep(string invokeClassName)
    {
        ArgumentException.ThrowIfNullOrEmpty(invokeClassName);

        if (_registry.TryGetValue(invokeClassName, out var factory))
            return factory();

        // Cache the resolved factory so subsequent calls don't re-reflect.
        var cached = _resolvedCache.GetOrAdd(invokeClassName, BuildFactory);
        return cached();
    }

    private static Func<IStep> BuildFactory(string invokeClassName)
    {
        var type = TypeNameResolver.Resolve(invokeClassName)
            ?? throw new WorkflowStepException(
                $"Step '{invokeClassName}' could not be resolved. " +
                $"Register it explicitly via TypeNameStepFactory.Register, or supply the assembly-qualified name.");

        if (!typeof(IStep).IsAssignableFrom(type))
            throw new WorkflowStepException(
                $"Type '{invokeClassName}' does not implement IStep.");

        return () => (IStep)(Activator.CreateInstance(type)
            ?? throw new WorkflowStepException($"Activator.CreateInstance returned null for '{invokeClassName}'."));
    }
}
