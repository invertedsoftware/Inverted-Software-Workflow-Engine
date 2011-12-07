// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND,
// EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A PARTICULAR PURPOSE.
//
// Copyright (C) Inverted Software(TM). All rights reserved.
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.DataObjects;
using InvertedSoftware.WorkflowEngine.Steps;
using InvertedSoftware.WorkflowEngine.Common.Security;

namespace InvertedSoftware.WorkflowEngine.Execution
{
    internal class StepExecutor
    {
        Impersonation impersonate = null;

        public StepExecutor()
        {
            impersonate = new Impersonation();
            impersonate.ImpersonationLogonType = WorkflowEngine.Common.Security.ImpersonationLogonType.LOGON32_LOGON_NEW_CREDENTIALS;
        }
        /// <summary>
        /// Run a workflow step
        /// </summary>
        /// <param name="workflowMessage">Message to pass to the step class</param>
        /// <param name="retryStepTimes">Times to retry on error</param>
        /// <param name="workflowStep">The step to run</param>
        internal void RunFrameworkStep(IWorkflowMessage workflowMessage, int retryStepTimes, ProcessorStep workflowStep, ProcessorJob currentJob, bool isCheckDepends)
        {
            bool impersonated = false;
            if (!string.IsNullOrEmpty(workflowStep.RunAsDomain) && !string.IsNullOrEmpty(workflowStep.RunAsUser) && !string.IsNullOrEmpty(workflowStep.RunAsPassword) && workflowStep.RunMode == FrameworkStepRunMode.STA)
                impersonated = impersonate.ImpersonateValidUser(workflowStep.RunAsUser, workflowStep.RunAsDomain, workflowStep.RunAsPassword);
            using (IStep step = StepFactory.GetStep(workflowStep.InvokeClass))
            {
                try
                {
                    workflowStep.RunStatus = FrameworkStepRunStatus.Loaded;
                    if (isCheckDepends)
                        WaitForDependents(workflowStep, currentJob);
                    workflowStep.StartDate = DateTime.Now;
                    step.RunStep(workflowMessage);
                    workflowStep.EndDate = DateTime.Now;
                    workflowStep.RunStatus = FrameworkStepRunStatus.Complete;
                    workflowStep.ExitMessage = "Complete";
                }
                catch (Exception e)
                {
                    workflowStep.RunStatus = FrameworkStepRunStatus.CompleteWithErrors;
                    workflowStep.ExitMessage = e.Message;
                    while (e.InnerException != null)
                    {
                        e = e.InnerException;
                        workflowStep.ExitMessage += '|' + e.Message;
                    }
                    switch (workflowStep.OnError)
                    {
                        case OnFrameworkStepError.RetryStep:
                            if (workflowStep.WaitBetweenRetriesMilliseconds > 0)
                                Thread.Sleep(workflowStep.WaitBetweenRetriesMilliseconds);
                            // Try the step again
                            retryStepTimes++;
                            if (retryStepTimes <= workflowStep.RetryTimes)
                                RunFrameworkStep(workflowMessage, retryStepTimes, workflowStep, currentJob, isCheckDepends);
                            break;
                        default:
                            // If this is running from a thread, do not throw the error up
                            if (workflowStep.RunMode == FrameworkStepRunMode.STA)
                                throw e;
                            else
                                Processor.ReportJobError(e, workflowStep, workflowMessage, currentJob);
                            break;
                    }
                }
            }

            if (impersonated)
                impersonate.UndoImpersonation();
        }

        /// <summary>
        /// Block untill all steps and groups this step depends on have finished
        /// </summary>
        /// <param name="workflowStep"></param>
        private void WaitForDependents(ProcessorStep workflowStep, ProcessorJob currentJob)
        {
            if (string.IsNullOrEmpty(workflowStep.DependsOn) &&
                string.IsNullOrEmpty(workflowStep.DependsOnGroup))
                return;

            var result = from ProcessorStep in currentJob.WorkFlowSteps
                         where ProcessorStep.RunStatus != FrameworkStepRunStatus.Complete &&
                         (workflowStep.DependsOn.Split(',').Contains(ProcessorStep.StepName) ||
                         workflowStep.DependsOnGroup.Split(',').Contains(ProcessorStep.Group))
                         select ProcessorStep;
            if (result.Count() > 0) //There are still steps to wait for
            {
                Thread.Sleep(100);
                workflowStep.RunStatusTime += 100;
                if (workflowStep.RunStatusTime <= workflowStep.WaitForDependsOnMilliseconds)
                    WaitForDependents(workflowStep, currentJob);
            }
        }
    }
}
