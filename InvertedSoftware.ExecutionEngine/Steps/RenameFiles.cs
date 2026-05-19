// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.Common.Exceptions;
using InvertedSoftware.WorkflowEngine.Messages;

namespace InvertedSoftware.WorkflowEngine.Steps;

/// <summary>Sample built-in step. Renames every *.jpg in the destination folder.</summary>
public class RenameFiles : IStep
{
    public void RunStep(IWorkflowMessage message, CancellationToken cancellationToken)
    {
        if (message is not ExampleMessage myMessage)
            throw new WorkflowStepException("IWorkflowMessage is of the wrong type");

        try
        {
            var po = new ParallelOptions { CancellationToken = cancellationToken };
            Parallel.ForEach(Directory.EnumerateFiles(myMessage.CopyFilesTo, "*.jpg"), po, f =>
            {
                try
                {
                    File.Move(f, $"{f}.{Guid.NewGuid()}");
                }
                catch (IOException) { }
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new WorkflowStepException(e.Message, e);
        }
    }

    public void Dispose() => GC.SuppressFinalize(this);
}
