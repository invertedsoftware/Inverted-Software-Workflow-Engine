using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.DataObjects;

namespace InvertedSoftware.WorkflowEngine.Execution
{
    internal interface IExecutor
    {
        ProcessorJob ProcessorJob { get; set; }
        void RunFrameworkJob(IWorkflowMessage workflowMessage, int retryJobTimes, bool isCheckDepends);
    }
}
