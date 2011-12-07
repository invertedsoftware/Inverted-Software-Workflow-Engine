using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.DataObjects;
using InvertedSoftware.WorkflowEngine.Common.Exceptions;

namespace InvertedSoftware.WorkflowEngine.Execution
{
    internal class SequentialExecutor : IExecutor
    {
        private StepExecutor stepExecutor = null;
        public ProcessorJob ProcessorJob { get; set; }

        public SequentialExecutor()
        {
            stepExecutor = new StepExecutor();
        }

        /// <summary>
        /// Runs a framework job
        /// </summary>
        /// <param name="workflowMessage">The Queue message</param>
        /// <param name="retryJobTimes">Times to retry the job or steps</param>
        public void RunFrameworkJob(IWorkflowMessage workflowMessage, int retryJobTimes, bool isCheckDepends)
        {
            // For each job, open a new ProcessorJob to keep track of logging
            ProcessorJob currentJob = (ProcessorJob)ProcessorJob.Clone();

            foreach (ProcessorStep workflowStep in currentJob.WorkFlowSteps)
            {
                try
                {
                    int retryStepTimes = 0;
                    workflowStep.RunStatus = FrameworkStepRunStatus.Waiting;
                    if (workflowStep.RunMode == FrameworkStepRunMode.STA)
                        stepExecutor.RunFrameworkStep(workflowMessage, retryStepTimes, workflowStep, currentJob, isCheckDepends);
                    else if (workflowStep.RunMode == FrameworkStepRunMode.MTA)
                        Task.Factory.StartNew(() => stepExecutor.RunFrameworkStep(workflowMessage, retryStepTimes, workflowStep, currentJob, isCheckDepends));
                }
                catch (Exception e)
                {
                    WorkflowException exception = new WorkflowException("Error in framework step " + workflowStep.StepName, e);
                    workflowStep.ExitMessage = e.Message;
                    switch (workflowStep.OnError)
                    {
                        case OnFrameworkStepError.RetryJob:
                            if (workflowStep.WaitBetweenRetriesMilliseconds > 0)
                                Thread.Sleep(workflowStep.WaitBetweenRetriesMilliseconds);
                            retryJobTimes++;
                            Processor.ReportJobError(e, workflowStep, workflowMessage, currentJob);
                            // Try the job again
                            if (retryJobTimes <= workflowStep.RetryTimes)
                                RunFrameworkJob(workflowMessage, retryJobTimes, isCheckDepends);
                            break;
                        case OnFrameworkStepError.Skip:
                            // Skip this step. Doing nothing here will skip it
                            Processor.ReportJobError(e, workflowStep, workflowMessage, currentJob);
                            break;
                        case OnFrameworkStepError.Exit:
                            // Push to to error queue with the error and exit the job
                            Processor.ReportJobError(e, workflowStep, workflowMessage, currentJob);
                            return;
                    }
                }
            }

            // If all steps ran without error report successful job compilation
            Processor.ReportJobComplete(workflowMessage, currentJob);
        }
    }
}
