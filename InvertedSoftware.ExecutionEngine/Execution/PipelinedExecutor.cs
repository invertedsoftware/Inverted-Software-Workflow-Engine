// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND,
// EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A PARTICULAR PURPOSE.
//
// Copyright (C) Inverted Software(TM). All rights reserved.
//
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using InvertedSoftware.WorkflowEngine.Common.Exceptions;
using InvertedSoftware.WorkflowEngine.DataObjects;
using InvertedSoftware.WorkflowEngine.Messages;

namespace InvertedSoftware.WorkflowEngine.Execution
{
    internal class PipelinedExecutor : IExecutor
    {
        private ProcessorJob processorJob;
        public ProcessorJob ProcessorJob
        {
            get
            {
                return processorJob;
            }
            set
            {
                processorJob = value;
                Task.Factory.StartNew(() => LoadPipeline());
            }
        }
        private StepExecutor stepExecutor = null;

        private CancellationTokenSource cts;
        private CancellationToken token;
        private BlockingCollection<PipelineInfo>[] stepsPipeline;
        private Task[] stepsTasks;
        private BlockingCollection<PipelineInfo> jobEndNodify = new BlockingCollection<PipelineInfo>();

        public PipelinedExecutor()
        {
            stepExecutor = new StepExecutor();
        }

        /// <summary>
        /// Set up all the tasks and blocking collections to process the steps
        /// When a job is picked up, simply pump in into the first BlockingCollection and let the pipeline run all the steps
        /// </summary>
        private void LoadPipeline()
        {
            cts = new CancellationTokenSource();
            token = cts.Token;
            stepsPipeline = new BlockingCollection<PipelineInfo>[processorJob.WorkFlowSteps.Count]; // We need a BlockingCollections to hold staps between executions
            stepsTasks = new Task[processorJob.WorkFlowSteps.Count]; // We need a Tasks to produce and consume steps from the BlockingCollections
            for (int i = 0; i < processorJob.WorkFlowSteps.Count; i++)
            {
                int stepCount = i;
                stepsPipeline[stepCount] = new BlockingCollection<PipelineInfo>();
                if (stepCount < processorJob.WorkFlowSteps.Count - 1)// The last step doesnt need to be added to the next BlockingCollection so simply finish the job
                    stepsTasks[stepCount] = Task.Factory.StartNew(() => RunPipelinedStep(stepsPipeline[stepCount], stepsPipeline[stepCount + 1], cts, stepCount));
                else
                    stepsTasks[stepCount] = Task.Factory.StartNew(() => RunPipelinedStep(stepsPipeline[stepCount], null, cts, stepCount));
            }
            Task.WaitAll(stepsTasks); // Wait until CancellationToken has been called
        }

        /// <summary>
        /// Run a step by consuming it from the currentPipelineStep BlockingCollection and then adding it to the nextPipelineStep BlockingCollection
        /// </summary>
        /// <param name="currentPipelineStep"></param>
        /// <param name="nextPipelineStep"></param>
        /// <param name="cts"></param>
        /// <param name="stepNumber"></param>
        private void RunPipelinedStep(BlockingCollection<PipelineInfo> currentPipelineStep,
            BlockingCollection<PipelineInfo> nextPipelineStep,
            CancellationTokenSource cts,
            int stepNumber)
        {
            var token = cts.Token;
            int currentStepNumber = stepNumber;

            try
            {
                // This loop will block until there are new elements in the currentPipelineStep
                foreach (var current in currentPipelineStep.GetConsumingEnumerable())
                {
                    if (token.IsCancellationRequested)
                        break;
                    PipelineInfo currentPipelineInfo = current; // The job info from the pipeline
                    ProcessorStep workflowStep = currentPipelineInfo.CurrentJob.WorkFlowSteps[stepNumber];
                    Task.Factory.StartNew<bool>(() => RunParalleledPipelinedStep(currentPipelineInfo, workflowStep, currentStepNumber))
                        .ContinueWith((result) =>
                        {
                            if (result.Result && nextPipelineStep != null) // Add The job info info to the next section of the pipeline.
                                nextPipelineStep.Add(currentPipelineInfo, token);
                            else if (result.Result && nextPipelineStep == null) // This is the last step in a job 
                            {
                                // If all steps ran without error report successful job compilation
                                Processor.ReportJobComplete(currentPipelineInfo.WorkflowMessage, currentPipelineInfo.CurrentJob);
                                jobEndNodify.Add(currentPipelineInfo);
                            }
                            else // Job error
                                jobEndNodify.Add(currentPipelineInfo);
                        });
                }
            }
            catch (Exception e)
            {
                cts.Cancel();
                if (!(e is OperationCanceledException))
                    throw;
            }
            finally
            {
                if (nextPipelineStep != null)
                    nextPipelineStep.CompleteAdding();
            }
        }

        /// <summary>
        /// Once a step has been picked up from the pipeline this method executes it.
        /// </summary>
        /// <param name="currentPipelineInfo"></param>
        /// <param name="workflowStep"></param>
        /// <param name="currentStepNumber"></param>
        /// <returns></returns>
        private bool RunParalleledPipelinedStep(PipelineInfo currentPipelineInfo, ProcessorStep workflowStep, int currentStepNumber)
        {
            try
            {
                workflowStep.RunStatus = FrameworkStepRunStatus.Waiting;
                if (workflowStep.RunMode == FrameworkStepRunMode.STA)
                    stepExecutor.RunFrameworkStep(currentPipelineInfo.WorkflowMessage, currentPipelineInfo.RetryStepTimes, workflowStep, currentPipelineInfo.CurrentJob, currentPipelineInfo.IsCheckDepends);
                else if (workflowStep.RunMode == FrameworkStepRunMode.MTA)
                    Task.Factory.StartNew(() => stepExecutor.RunFrameworkStep(currentPipelineInfo.WorkflowMessage, currentPipelineInfo.RetryStepTimes, workflowStep, currentPipelineInfo.CurrentJob, currentPipelineInfo.IsCheckDepends));
                return true;
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
                        currentPipelineInfo.RetryJobTimes++;
                        Processor.ReportJobError(e, workflowStep, currentPipelineInfo.WorkflowMessage, currentPipelineInfo.CurrentJob);
                        // Try the job again
                        if (currentPipelineInfo.RetryJobTimes <= workflowStep.RetryTimes)
                            RunFrameworkJob(currentPipelineInfo.WorkflowMessage, currentPipelineInfo.RetryJobTimes, currentPipelineInfo.IsCheckDepends);
                        return false;
                    case OnFrameworkStepError.Skip:
                        // Skip this step. Doing nothing here will skip it
                        Processor.ReportJobError(e, workflowStep, currentPipelineInfo.WorkflowMessage, currentPipelineInfo.CurrentJob);
                        return true;
                    case OnFrameworkStepError.Exit:
                        // Push to to error queue with the error and do not continue to the next pipe
                        Processor.ReportJobError(e, workflowStep, currentPipelineInfo.WorkflowMessage, currentPipelineInfo.CurrentJob);
                        return false;
                    default:
                        return false;
                }
            }
        }

        /// <summary>
        /// Run a new framework job
        /// </summary>
        /// <param name="workflowMessage"></param>
        /// <param name="retryJobTimes"></param>
        /// <param name="isCheckDepends"></param>
        public void RunFrameworkJob(IWorkflowMessage workflowMessage, int retryJobTimes, bool isCheckDepends)
        {
            if (stepsPipeline == null)
                return;
            // For each job, open a new ProcessorJob to keep track of logging
            ProcessorJob currentJob = (ProcessorJob)processorJob.Clone();
            // Add the job for the first pipeline
            PipelineInfo pipelineInfo = new PipelineInfo()
            {
                CurrentJob = currentJob,
                IsCheckDepends = isCheckDepends,
                RetryJobTimes = retryJobTimes,
                RetryStepTimes = 0,
                WorkflowMessage = workflowMessage
            };
            stepsPipeline[0].Add(pipelineInfo);
            // Block untill a job is finished
            // This is done to regulate execution rate
            jobEndNodify.Take();
        }
    }
}
