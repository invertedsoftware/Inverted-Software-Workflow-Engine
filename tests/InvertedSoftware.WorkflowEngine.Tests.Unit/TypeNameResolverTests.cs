// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.Queue;
using Xunit;

namespace InvertedSoftware.WorkflowEngine.Tests.Unit;

/// <summary>
/// Verifies the shared <see cref="TypeNameResolver"/> behaves correctly and caches.
/// </summary>
public class TypeNameResolverTests
{
    [Fact]
    public void Resolve_Returns_Same_Instance_On_Repeated_Calls()
    {
        var name = typeof(TypeNameResolverTests).FullName!;
        var first = TypeNameResolver.Resolve(name);
        var second = TypeNameResolver.Resolve(name);
        Assert.Same(first, second);
    }

    [Fact]
    public void Resolve_Returns_Null_For_Unknown_Type()
    {
        var result = TypeNameResolver.Resolve("Definitely.Not.A.Real.Type, NoSuchAssembly");
        Assert.Null(result);
    }

    [Fact]
    public void ResolveOrThrow_Throws_With_Diagnostic_On_Unknown_Type()
    {
        var ex = Assert.Throws<MessageDeserializationException>(
            () => TypeNameResolver.ResolveOrThrow("Definitely.Not.A.Real.Type"));
        Assert.Contains("Definitely.Not.A.Real.Type", ex.Message);
    }

    [Fact]
    public void Resolve_Finds_Engine_Types_Without_AssemblyQualifiedName()
    {
        // The Common types live in InvertedSoftware.WorkflowEngine.Common.dll, which is
        // referenced indirectly. The resolver should still find it by full name.
        var t = TypeNameResolver.Resolve("InvertedSoftware.WorkflowEngine.Common.Exceptions.WorkflowException");
        Assert.NotNull(t);
    }
}
