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
using System.Messaging;
using System.ServiceModel;

using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Config;
using InvertedSoftware.WorkflowEngine.DataObjects;
using InvertedSoftware.WorkflowEngine.Common.Exceptions;


namespace InvertedSoftware.WorkflowEngine
{
    public static class FrameworkManager
    {
        /// <summary>
        /// Add a job for the framework to process
        /// </summary>
        /// <param name="jobName">The name of the job in the framework's workflow file</param>
        /// <param name="message">A class containing the message data</param>
        public static void AddFrameworkJob(string jobName, IWorkflowMessage message)
        {
            // Add a message to the Queue
            ProcessorJob processorJob = new ProcessorJob() { JobName = jobName, CreatedDate = DateTime.Now };
            WorkflowConfiguration.LoadFrameworkConfig(processorJob);
            ProcessorQueue processorQueue = GetActiveQueue(processorJob, QueueOperationType.Delivery);
            MessageQueue workflowQueue = new MessageQueue(processorQueue.MessageQueue);
            MessageQueueTransaction transaction = new MessageQueueTransaction();
            try
            {
                if (processorQueue.MessageQueueType == MessageQueueType.Transactional)
                {
                    transaction.Begin();
                    workflowQueue.Send(message, jobName, transaction);
                    transaction.Commit();
                }
                else
                {
                    workflowQueue.Send(message, jobName);
                }
            }
            catch (Exception e)
            {
                if (processorQueue.MessageQueueType == MessageQueueType.Transactional &&
                    transaction.Status == MessageQueueTransactionStatus.Pending)
                    transaction.Abort();
                throw new WorkflowException("Error adding message to Queue", e);
            }
            finally
            {
                transaction.Dispose();
                workflowQueue.Dispose();
            }
        }

        /// <summary>
        /// Re adds a Job that ran in the past for re processing
        /// This will make sure to ignore any "DependsOn" rules and will only run active steps
        /// </summary>
        /// <param name="jobName">The name of the job in the framework's workflow file</param>
        /// <param name="message">A class containing the message data</param>
        public static void ReAddFrameworkJob(string jobName, IWorkflowMessage message)
        {
            // Add a message to the Queue
            ProcessorJob processorJob = new ProcessorJob() { JobName = jobName };
            WorkflowConfiguration.LoadFrameworkConfig(processorJob);
            ProcessorQueue processorQueue = GetActiveQueue(processorJob, QueueOperationType.Delivery);
            MessageQueue workflowQueue = new MessageQueue(processorQueue.MessageQueue);
            MessageQueueTransaction transaction = new MessageQueueTransaction();
            try
            {
                if (processorQueue.MessageQueueType == MessageQueueType.Transactional)
                {
                    transaction.Begin();
                    message.JobID = -message.JobID; // Negative JobID indicates a re run
                    workflowQueue.Send(message, jobName, transaction);
                    transaction.Commit();
                }
                else
                {
                    message.JobID = -message.JobID; // Negative JobID indicates a re run
                    workflowQueue.Send(message, jobName);
                }
            }
            catch (Exception e)
            {
                if (processorQueue.MessageQueueType == MessageQueueType.Transactional &&
                    transaction.Status == MessageQueueTransactionStatus.Pending)
                    transaction.Abort();
                throw new WorkflowException("Error adding message to Queue", e);
            }
            finally
            {
                transaction.Dispose();
                workflowQueue.Dispose();
            }
        }

        /// <summary>
        /// Get the currently active Queue to pickup or deliver messages
        /// </summary>
        /// <param name="processorJob">The current loaded configuration</param>
        /// <param name="queueOperationType">The type of operation to execute on the Queue</param>
        /// <returns>The current active ProcessorQueue for the Operation Type</returns>
        public static ProcessorQueue GetActiveQueue(ProcessorJob processorJob, QueueOperationType queueOperationType)
        {
            // For backward compatibility if the master Queue is present return it
            if (!string.IsNullOrEmpty(processorJob.MessageQueue) && !string.IsNullOrEmpty(processorJob.ErrorQueue))
                return new ProcessorQueue()
                {
                    MessageQueue = processorJob.MessageQueue,
                    ErrorQueue = processorJob.ErrorQueue,
                    PoisonQueue = processorJob.PoisonQueue,
                    CompletedQueue = processorJob.CompletedQueue,
                    MessageQueueType = processorJob.MessageQueueType
                };

            //If this is a delivery, deliver to the first available queue
            //If this is a pickup, pick it from the queues in a reverse order
            // Get the first queue with messages
            ProcessorQueue queue = new ProcessorQueue();
            if (queueOperationType == QueueOperationType.Pickup)
                processorJob.ProcessorQueues.Reverse();

            foreach (ProcessorQueue processorQueue in processorJob.ProcessorQueues)
            {
                MessageQueue workflowQueue = new MessageQueue(processorQueue.MessageQueue);
                queue = processorQueue;

                try
                {
                    using (MessageQueue msmq = new MessageQueue(processorQueue.MessageQueue))
                    {
                        msmq.Peek(new TimeSpan(0));
                        break;
                    }
                }
                catch (MessageQueueException e)
                {
                    if (e.MessageQueueErrorCode == MessageQueueErrorCode.IOTimeout)
                    {
                        // The queue is is available and empty it can be used for delivery
                        if (queueOperationType == QueueOperationType.Delivery)
                            break;
                    }
                }
                catch (Exception) { }
            }
            // Reverse the queue list back to the original order
            if (queueOperationType == QueueOperationType.Pickup)
                processorJob.ProcessorQueues.Reverse();

            return queue;
        }

        /// <summary>
        /// Add a message containing the error to the error queue and the original message to the poison queue
        /// </summary>
        /// <param name="jobName"></param>
        /// <param name="message"></param>
        internal static void AddFrameworkError(ProcessorJob processorJob, IWorkflowMessage message, WorkflowErrorMessage errorMessage)
        {
            try
            {
                WorkflowConfiguration.LoadFrameworkConfig(processorJob);
                ProcessorQueue processorQueue = GetActiveQueue(processorJob, QueueOperationType.Delivery);
                MsmqPoisonMessageException error = new MsmqPoisonMessageException() { Source = processorJob.JobName };
                error.Data["processorQueue"] = processorQueue;
                error.Data["message"] = message;
                error.Data["errorMessage"] = errorMessage;

                QueueOperationsHandler.HandleError(error);
            }
            catch (Exception)
            {

            }
        }

        internal static void AddFrameworkJobComplete(ProcessorJob processorJob, IWorkflowMessage message)
        {
            try
            {
                WorkflowConfiguration.LoadFrameworkConfig(processorJob);
                ProcessorQueue processorQueue = GetActiveQueue(processorJob, QueueOperationType.Delivery);
                QueueOperationsHandler.HandleComplete(processorQueue, message);
            }
            catch (Exception)
            {

            }
        }
    }

    /// <summary>
    /// Contains the information passed down an execution pipeline
    /// </summary>
    internal struct PipelineInfo
    {
        public IWorkflowMessage WorkflowMessage { get; set; }
        public int RetryStepTimes { get; set; }
        public int RetryJobTimes { get; set; }
        public ProcessorJob CurrentJob { get; set; }
        public bool IsCheckDepends { get; set; }
    }
}
