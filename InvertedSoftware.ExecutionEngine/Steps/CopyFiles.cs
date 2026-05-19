// Copyright (c) Inverted Software. All rights reserved.

using InvertedSoftware.WorkflowEngine.Common.Exceptions;
using InvertedSoftware.WorkflowEngine.Messages;

namespace InvertedSoftware.WorkflowEngine.Steps;

/// <summary>Sample built-in step. Copies all files from one folder to another.</summary>
public class CopyFiles : IStep
{
    public void RunStep(IWorkflowMessage message, CancellationToken cancellationToken)
    {
        if (message is not ExampleMessage myMessage)
            throw new WorkflowStepException("IWorkflowMessage is of the wrong type");

        try
        {
            var po = new ParallelOptions { CancellationToken = cancellationToken };
            Parallel.ForEach(Directory.EnumerateFiles(myMessage.CopyFilesFrom, "*"), po, f =>
            {
                try
                {
                    File.Copy(f, Path.Combine(myMessage.CopyFilesTo, Path.GetFileName(f)), overwrite: true);
                }
                catch (IOException)
                {
                    // Swallow per-file IO errors (e.g. file in use); the cooperative cancel still propagates.
                }
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
