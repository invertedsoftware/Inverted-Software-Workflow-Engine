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
using System.Messaging;
using System.Transactions;
using System.Threading.Tasks;
using System.Collections.Concurrent;

using InvertedSoftware.WorkflowEngine.DataObjects;
using InvertedSoftware.WorkflowEngine.Common.Security;
using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Steps;
using InvertedSoftware.WorkflowEngine.Common.Exceptions;
using InvertedSoftware.WorkflowEngine.Config;
using InvertedSoftware.WorkflowEngine.Common;
using InvertedSoftware.WorkflowEngine.Execution;




namespace InvertedSoftware.WorkflowEngine
{
	/// <summary>
	/// The class in charge of processing framework jobs
	/// </summary>
	public class Processor
	{
		/// <summary>
		/// Indicates to the framework to stop processing at the end of the current job
		/// </summary>
		public bool StopProcess { get; set; }
		/// <summary>
		/// Get the count of jobs currently running
		/// </summary>
		public int JobsRunning;
		/// <summary>
		/// Indicates that the framework is currently on
		/// </summary>
		public bool FrameworkOn { get; set; }
		private ProcessorJob processorJob { get; set; }
		Impersonation impersonate = null;
		ProcessorQueue ProcessorQueue { get; set; }
		// Threading variables
		private static Semaphore pool; // Limit the number of threads that can work on jobs

		private IExecutor executor = null;
		/// <summary>
		/// Constracor
		/// </summary>
		public Processor()
		{
			ProcessorQueue = new ProcessorQueue();
			impersonate = new Impersonation();
			impersonate.ImpersonationLogonType = WorkflowEngine.Common.Security.ImpersonationLogonType.LOGON32_LOGON_NEW_CREDENTIALS;
			if (Utils.PROCESSOR_COUNT > 1 && EngineConfiguration.UsePipelinedOnMulticore)
				executor = new PipelinedExecutor();
			else
				executor = new SequentialExecutor();
		}

		/// <summary>
		/// This fires in the rare event when the queue goes offline. The framework will then look for another queue
		/// This will simply soft restart the processor, which will point to the next available queue
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		void ProcessorQueue_ProcessorQueueChanged(object sender, ProcessorQueueChangedEventArgs e)
		{
			//The queue have changed. restart the framework pointing to the new Queue
			ProcessorQueue.ProcessorQueueChanged -= new ProcessorQueue.ProcessorQueueEventHandler(ProcessorQueue_ProcessorQueueChanged);
			StopFramework(true);
			Thread.Sleep(10000); // Give the framework ten seconds to commit any transactions
			StopProcess = false;
			StartFramework(processorJob.JobName);
		}

		/// <summary>
		/// This method starts the framework and blocks.
		/// Call this from an exe or a windows service on a new thread.
		/// </summary>
		/// <param name="jobName">The workflow job name</param>
		public void StartFramework(string jobName)
		{
			processorJob = new ProcessorJob();
			processorJob.JobName = jobName;
			pool = new Semaphore(EngineConfiguration.FrameworkMaxThreads, EngineConfiguration.FrameworkMaxThreads);
			JobsRunning = 0;
			// Load the config file and start processing jobs
			WorkflowConfiguration.LoadFrameworkConfig(processorJob);
			FrameworkOn = true;
			LoadActiveQueue();
			ProcessorQueue.ProcessorQueueChanged += new ProcessorQueue.ProcessorQueueEventHandler(ProcessorQueue_ProcessorQueueChanged);
			executor.ProcessorJob = processorJob;
			RunFramework(5, 0, 5);
			FrameworkOn = false;
		}

		/// <summary>
		/// Stop the framework from processing any more jobs
		/// </summary>
		/// <param name="isSoftExit">Block untill the current jobs are done</param>
		public void StopFramework(bool isSoftExit)
		{
			StopProcess = true;
			//Wait until the framework stopped
			while (isSoftExit && JobsRunning > 0)
				Thread.Sleep(500);
		}

		/// <summary>
		/// Starts to run the framework
		/// </summary>
		/// <param name="exceptionsBeforeExit">Number of times to try and run the framework</param>
		/// <param name="currentExceptionNumber">Current Exception Number</param>
		/// <param name="pauseFor">Wait between retrys in seconds</param>
		private void RunFramework(int exceptionsBeforeExit, int currentExceptionNumber, int pauseFor)
		{
			try
			{
				MessageQueue workflowQueue = new MessageQueue(ProcessorQueue.MessageQueue);
				Guid QID = workflowQueue.Id;
				workflowQueue.Dispose();
				if (ProcessorQueue.MessageQueueType == MessageQueueType.Transactional)
					if (ProcessorQueue.MessageQueue.StartsWith(@".\"))
						RunTransactionalFramework();
					else
						RunRemoteTransactionalFramework();
				else
					RunFramework();
			}
			catch (Exception e)
			{
				currentExceptionNumber++;
				if (currentExceptionNumber <= exceptionsBeforeExit)
				{
					Thread.Sleep(pauseFor * 1000);
					RunFramework(exceptionsBeforeExit, currentExceptionNumber, pauseFor);
				}
				else
					throw new FrameworkFatalException("Fatal Error in the framework: " + e.Message, e);
			}
		}

		/// <summary>
		/// Starts to listen to a local Transactional Queue and process Jobs
		/// </summary>
		private void RunTransactionalFramework()
		{
			using (MessageQueue workflowQueue = new MessageQueue(ProcessorQueue.MessageQueue))
			{
				((XmlMessageFormatter)workflowQueue.Formatter).TargetTypes = new System.Type[] { Type.GetType(Utils.MESSAGE_BASE_TYPE + "." + processorJob.MessageClass) };
				workflowQueue.MessageReadPropertyFilter.Priority = true;

				while (!StopProcess)
				{
					// Read a message from the Queue and process it
					MessageQueueTransaction transaction = new MessageQueueTransaction();
					try
					{
						pool.WaitOne();
						transaction.Begin();
						Message queueMessage = workflowQueue.Receive(transaction);
						//Start the job in a new thread. Do not exceed AppsConfig.FrameworkMaxThreads 
						Task.Factory.StartNew(() => RunFrameworkJob(queueMessage, transaction));
						Interlocked.Increment(ref JobsRunning);
					}
					catch (MessageQueueException e)
					{
						Interlocked.Decrement(ref JobsRunning);
						pool.Release();
						transaction.Abort();
						throw new WorkflowException("Error getting framework job from queue: An internal Message Queuing error occured", e);
					}
					catch (InvalidOperationException e)
					{
						Interlocked.Decrement(ref JobsRunning);
						pool.Release();
						transaction.Commit();
						throw new WorkflowException("Error begining transaction: The transaction has already been started.", e);
					}
					catch (Exception e)
					{
						Interlocked.Decrement(ref JobsRunning);
						pool.Release();
						transaction.Abort();
						throw new WorkflowException("General Error Starting Framework Job.", e);
					}
				}
			}
		}

		/// <summary>
		/// Starts to listen to a remote Transactional Queue and process Jobs
		/// This is not tested yet
		/// </summary>
		private void RunRemoteTransactionalFramework()
		{
			MessageQueue workflowQueue = new MessageQueue(ProcessorQueue.MessageQueue);
			((XmlMessageFormatter)workflowQueue.Formatter).TargetTypes = new System.Type[] { Type.GetType(Utils.MESSAGE_BASE_TYPE + "." + processorJob.MessageClass) };
			workflowQueue.MessageReadPropertyFilter.Priority = true;
			workflowQueue.PeekCompleted += new PeekCompletedEventHandler(workflowQueue_PeekCompleted);
			pool.WaitOne();
			workflowQueue.BeginPeek();
			while (!StopProcess)
			{
				Thread.Sleep(TimeSpan.FromSeconds(10));
			}
		}

		void workflowQueue_PeekCompleted(object sender, PeekCompletedEventArgs e)
		{
			MessageQueue workflowQueue = (MessageQueue)sender;
			using (TransactionScope txScope = new TransactionScope(TransactionScopeOption.RequiresNew))
			{
				try
				{
					Message queueMessage = workflowQueue.Receive(MessageQueueTransactionType.Automatic);
					Task.Factory.StartNew(() => RunFrameworkJob(queueMessage, null));
					Interlocked.Increment(ref JobsRunning);
					txScope.Complete();
				}
				catch (MessageQueueException ex)
				{
					Interlocked.Decrement(ref JobsRunning);
					pool.Release();
					if (ex.MessageQueueErrorCode == MessageQueueErrorCode.QueueNotAvailable)
						LoadActiveQueue();
					else
						throw new WorkflowException("Error getting framework job from queue: An internal Message Queuing error occured", ex);
				}
				catch (InvalidOperationException ex)
				{
					Interlocked.Decrement(ref JobsRunning);
					pool.Release();
					throw new WorkflowException("Error begining transaction: The transaction has already been started.", ex);
				}
				catch (Exception ex)
				{
					Interlocked.Decrement(ref JobsRunning);
					pool.Release();
					throw new WorkflowException("General Error Starting Framework Job.", ex);
				}
			}

			if (!StopProcess)
			{
				pool.WaitOne();
				workflowQueue.BeginPeek();
			}
		}

		private void RunFramework()
		{
			using (MessageQueue workflowQueue = new MessageQueue(ProcessorQueue.MessageQueue))
			{
				((XmlMessageFormatter)workflowQueue.Formatter).TargetTypes = new System.Type[] { Type.GetType(Utils.MESSAGE_BASE_TYPE + processorJob.MessageClass) };
				workflowQueue.MessageReadPropertyFilter.Priority = true;

				while (!StopProcess)
				{
					// Read a message from the Queue and process it
					try
					{
						pool.WaitOne();
						Message queueMessage = workflowQueue.Receive();
						//Start the job in a new thread. Do not exceed AppsConfig.FrameworkMaxThreads                    
						Task.Factory.StartNew(() => RunFrameworkJob(queueMessage, null));
						Interlocked.Increment(ref JobsRunning);
					}
					catch (MessageQueueException e)
					{
						Interlocked.Decrement(ref JobsRunning);
						pool.Release();
						throw new WorkflowException("Error getting framework job from queue: An internal Message Queuing error occured", e);
					}
					catch (Exception e)
					{
						Interlocked.Decrement(ref JobsRunning);
						pool.Release();
						throw new WorkflowException("General Error Starting Framework Job.", e);
					}
				}
			}
		}

		/// <summary>
		/// Runs a single job thread
		/// </summary>
		/// <param name="data"></param>
		private void RunFrameworkJob(Message queueMessage, MessageQueueTransaction transaction)
		{
			JobTimer jobTimer = new JobTimer(processorJob.MaxRunTimeMilliseconds, Thread.CurrentThread); // The timer terminates jobs that take too much time to run
			try
			{
				int retryJobTimes = 0;
				IWorkflowMessage workflowMessage = queueMessage.Body as IWorkflowMessage;
				bool isCheckDepends = true;
				if (workflowMessage.JobID < 0) // This is a re-run
				{
					workflowMessage.JobID = -workflowMessage.JobID;
					isCheckDepends = false;
				}
				executor.RunFrameworkJob(workflowMessage, retryJobTimes, isCheckDepends);
				retryJobTimes = 0;
			}
			catch (Exception e)
			{
				new WorkflowException("Error running framework job", e);
			}
			finally
			{
				pool.Release();
				jobTimer.StopTimer();
				Interlocked.Decrement(ref JobsRunning);
				// Commit removing of the queue message at the end of the job
				try
				{
					if (transaction != null)
					{
						transaction.Commit();
						transaction.Dispose();
					}
				}
				catch (InvalidOperationException e)
				{
					new WorkflowException("Error commiting queue transaction: The transaction you are trying to commit has not started.", e);
				}
				catch (MessageQueueException e)
				{
					new WorkflowException("Error commiting queue transaction: An internal Message Queuing error occured.", e);
				}
			}
			//Find out the active Queue for the next Job
			LoadActiveQueue();
		}

		/// <summary>
		/// Reports a job error
		/// </summary>
		/// <param name="e">The exception to report</param>
		/// <param name="step">The step the exception accrued</param>
		/// <param name="workflowMessage">The original message pulled from the queue</param>
		public static void ReportJobError(Exception e, ProcessorStep workflowStep, IWorkflowMessage workflowMessage, ProcessorJob currentJob)
		{
			// Push an error message to the error Queue
			WorkflowErrorMessage errorMessage = new WorkflowErrorMessage() { ExceptionMessage = e.Message, JobName = currentJob.JobName, StepName = workflowStep.StepName };

			//if e contains inner exceptions, add those messages
			while (e.InnerException != null)
			{
				//assign e to the inner exception - recursive
				e = e.InnerException;
				errorMessage.ExceptionMessage += '|' + e.Message;
			}

			FrameworkManager.AddFrameworkError(currentJob, workflowMessage, errorMessage);
		}

		public static void ReportJobComplete(IWorkflowMessage workflowMessage, ProcessorJob currentJob)
		{
			if (currentJob.NotifyComplete &&
				currentJob.WorkFlowSteps.Where(s => s.RunStatus == FrameworkStepRunStatus.Complete).Count() == currentJob.WorkFlowSteps.Count())
				FrameworkManager.AddFrameworkJobComplete(currentJob, workflowMessage);
		}

		/// <summary>
		/// Load the active Queue for the next Job
		/// </summary>
		private void LoadActiveQueue()
		{
			ProcessorQueue ActiveQueue = FrameworkManager.GetActiveQueue(processorJob, QueueOperationType.Pickup);
			ProcessorQueue.ErrorQueue = ActiveQueue.ErrorQueue;
			ProcessorQueue.MessageQueueType = ActiveQueue.MessageQueueType;
			ProcessorQueue.MessageQueue = ActiveQueue.MessageQueue; // This will fire an event if the Queue changed
		}
	}
}
