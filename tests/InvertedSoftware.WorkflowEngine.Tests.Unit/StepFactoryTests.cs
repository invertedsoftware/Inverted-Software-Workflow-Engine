// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.Common.Exceptions;
using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Steps;
using Xunit;

namespace InvertedSoftware.WorkflowEngine.Tests.Unit;

public class StepFactoryTests
{
    [Fact]
    public void Registered_Step_Returns_Factory_Output()
    {
        var instance = new TestStep();
        var factory = new TypeNameStepFactory().Register<TestStep>("explicit-key", () => instance);

        var resolved = factory.GetStep("explicit-key");

        Assert.Same(instance, resolved);
    }

    [Fact]
    public void Unregistered_Type_Resolves_By_FullName_And_Caches()
    {
        var factory = new TypeNameStepFactory();

        var first = factory.GetStep(typeof(TestStep).FullName!);
        var second = factory.GetStep(typeof(TestStep).FullName!);

        Assert.IsType<TestStep>(first);
        Assert.IsType<TestStep>(second);
        // Two different instances (Activator.CreateInstance returns a fresh one)
        // but resolution shouldn't have allocated a new Func each time.
    }

    [Fact]
    public void Unknown_Step_Throws_WorkflowStepException()
    {
        var factory = new TypeNameStepFactory();
        var ex = Assert.Throws<WorkflowStepException>(() => factory.GetStep("Not.A.Real.Step.Type"));
        Assert.Contains("Not.A.Real.Step.Type", ex.Message);
    }

    [Fact]
    public void Type_That_Does_Not_Implement_IStep_Throws()
    {
        var factory = new TypeNameStepFactory();
        var ex = Assert.Throws<WorkflowStepException>(() => factory.GetStep(typeof(string).FullName!));
        Assert.Contains("does not implement IStep", ex.Message);
    }

    public sealed class TestStep : IStep
    {
        public void RunStep(IWorkflowMessage message, CancellationToken cancellationToken) { }
        public void Dispose() { }
    }
}
