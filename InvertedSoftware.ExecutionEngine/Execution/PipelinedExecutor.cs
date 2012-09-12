// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND,
// EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A PARTICULAR PURPOSE.
//
// Copyright (C) Inverted Software(TM). All rights reserved.
//
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

using InvertedSoftware.WorkflowEngine.Common.Exceptions;
using InvertedSoftware.WorkflowEngine.Config;
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
                LoadPipeline();
            }
        }
        private StepExecutor stepExecutor = new StepExecutor();
        private CancellationTokenSource cts=  new CancellationTokenSource();
        private List<TransformBlock<PipelineInfo, PipelineInfo>> workerBlocks = new List<TransformBlock<PipelineInfo, PipelineInfo>>();

        private void LoadPipeline()
        {
            for (int i = 0; i < processorJob.WorkFlowSteps.Count; i++)
            {
                TransformBlock<PipelineInfo, PipelineInfo> workerBlock = new TransformBlock<PipelineInfo, PipelineInfo>(pi =>
                {
                    return RunParalleledPipelinedStep(pi);
                },
                new ExecutionDataflowBlockOptions
                {
                    CancellationToken = cts.Token,
                    MaxDegreeOfParallelism = EngineConfiguration.FrameworkMaxThreads
                });

                if (workerBlocks.Count > 0)
                {
                    // Connect the dataflow blocks to form a pipeline. 
                    workerBlocks[workerBlocks.Count - 1].LinkTo<PipelineInfo>(workerBlock);
                }
                workerBlocks.Add(workerBlock);
            }

            ProcessJobEnded();
        }

        private async void ProcessJobEnded() 
        {
            while (true)
            {
                PipelineInfo input = await workerBlocks[workerBlocks.Count - 1].ReceiveAsync(cts.Token);
                if (input.IsInProcess) // All the steps where successful
                    Processor.ReportJobComplete(input.WorkflowMessage, input.CurrentJob);
            }
        }

        /// <summary>
        /// Run a step
        /// </summary>
        /// <param name="currentPipelineInfo"></param>
        /// <param name="workflowStep"></param>
        /// <param name="currentStepNumber"></param>
        /// <returns>The PipelineInfo to be passed to the next step</returns>
        private PipelineInfo RunParalleledPipelinedStep(PipelineInfo currentPipelineInfo)
        {
            if (!currentPipelineInfo.IsInProcess) // This job has been canceled.
                return currentPipelineInfo;

            ProcessorStep workflowStep = currentPipelineInfo.CurrentJob.WorkFlowSteps[currentPipelineInfo.CurrentStepNumber];
            try
            {
                workflowStep.RunStatus = FrameworkStepRunStatus.Waiting;
                if (workflowStep.RunMode == FrameworkStepRunMode.STA)
                    stepExecutor.RunFrameworkStep(currentPipelineInfo.WorkflowMessage, currentPipelineInfo.RetryStepTimes, workflowStep, currentPipelineInfo.CurrentJob, currentPipelineInfo.IsCheckDepends);
                else if (workflowStep.RunMode == FrameworkStepRunMode.MTA)
                    Task.Factory.StartNew(() => stepExecutor.RunFrameworkStep(currentPipelineInfo.WorkflowMessage, currentPipelineInfo.RetryStepTimes, workflowStep, currentPipelineInfo.CurrentJob, currentPipelineInfo.IsCheckDepends));
             
                currentPipelineInfo.CurrentStepNumber++;
                return currentPipelineInfo;
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
                        currentPipelineInfo.IsInProcess = false;
                        return currentPipelineInfo;
                    case OnFrameworkStepError.Skip:
                        // Skip this step. Doing nothing here will skip it
                        Processor.ReportJobError(e, workflowStep, currentPipelineInfo.WorkflowMessage, currentPipelineInfo.CurrentJob);
                        currentPipelineInfo.CurrentStepNumber++;
                        return currentPipelineInfo;
                    case OnFrameworkStepError.Exit:
                        // Push to to error queue with the error and do not continue to the next pipe
                        Processor.ReportJobError(e, workflowStep, currentPipelineInfo.WorkflowMessage, currentPipelineInfo.CurrentJob);
                        currentPipelineInfo.IsInProcess = false;
                        return currentPipelineInfo;
                    default:
                        return currentPipelineInfo;
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
            // For each job, open a new ProcessorJob to keep track of logging
            ProcessorJob currentJob = (ProcessorJob)processorJob.Clone();
            // Add the job for the first pipeline
            PipelineInfo pipelineInfo = new PipelineInfo()
            {
                CurrentJob = currentJob,
                IsCheckDepends = isCheckDepends,
                RetryJobTimes = retryJobTimes,
                RetryStepTimes = 0,
                WorkflowMessage = workflowMessage,
                IsInProcess = true, 
                CurrentStepNumber = 0
            };
            int currentJobsCount =  workerBlocks[0].InputCount;
            workerBlocks[0].Post(pipelineInfo);

            // Block untill a job is finished
            // This is done to regulate execution rate
            workerBlocks[workerBlocks.Count - 1].OutputAvailableAsync(cts.Token).Wait();
        }
    }
}
