// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND,
// EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A PARTICULAR PURPOSE.
//
// Copyright (C) Inverted Software(TM). All rights reserved.
//
using System;
using System.Xml;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InvertedSoftware.WorkflowEngine.DataObjects;
using InvertedSoftware.WorkflowEngine.Common;


namespace InvertedSoftware.WorkflowEngine.Config
{
	public class WorkflowConfiguration
	{
		/// <summary>
		/// Load the framework config from workflow.xml
		/// </summary>
		internal static void LoadFrameworkConfig(ProcessorJob processorJob)
		{
			XmlDocument doc = new XmlDocument();
			XmlTextReader reader = new XmlTextReader(EngineConfiguration.FrameworkConfigLocation);
			doc.Load(reader);
			reader.Close();
			// Load the job
			XmlNode jobNode = doc.SelectSingleNode("//Job[@Name='" + processorJob.JobName + "']");
			processorJob.MessageClass = jobNode.Attributes["MessageClass"].Value;
			if (jobNode.Attributes["MessageQueue"] != null && !string.IsNullOrEmpty(jobNode.Attributes["MessageQueue"].Value))
				processorJob.MessageQueue = jobNode.Attributes["MessageQueue"].Value;
			if (jobNode.Attributes["ErrorQueue"] != null && !string.IsNullOrEmpty(jobNode.Attributes["ErrorQueue"].Value))
				processorJob.ErrorQueue = jobNode.Attributes["ErrorQueue"].Value;
			if (jobNode.Attributes["PoisonQueue"] != null && !string.IsNullOrEmpty(jobNode.Attributes["PoisonQueue"].Value))
				processorJob.PoisonQueue = jobNode.Attributes["PoisonQueue"].Value;
			if (jobNode.Attributes["CompletedQueue"] != null && !string.IsNullOrEmpty(jobNode.Attributes["CompletedQueue"].Value))
				processorJob.CompletedQueue = jobNode.Attributes["CompletedQueue"].Value;
			if (jobNode.Attributes["NotifyComplete"] != null && !string.IsNullOrEmpty(jobNode.Attributes["NotifyComplete"].Value))
			{
				bool notifyComplete = false;
				if (bool.TryParse(jobNode.Attributes["NotifyComplete"].Value, out notifyComplete))
					processorJob.NotifyComplete = notifyComplete;
			}

			if (jobNode.Attributes["MaxRunTimeMilliseconds"] != null && !string.IsNullOrEmpty(jobNode.Attributes["MaxRunTimeMilliseconds"].Value))
			{
				int maxRunTimeMilliseconds = 0;
				if (int.TryParse(jobNode.Attributes["MaxRunTimeMilliseconds"].Value, out maxRunTimeMilliseconds))
					processorJob.MaxRunTimeMilliseconds = maxRunTimeMilliseconds;
			}
			if (jobNode.Attributes["MessageQueueType"] != null && !string.IsNullOrEmpty(jobNode.Attributes["MessageQueueType"].Value))
				processorJob.MessageQueueType = (MessageQueueType)Enum.Parse(typeof(MessageQueueType), jobNode.Attributes["MessageQueueType"].Value, true);
			//Load the Queues
			XmlNode queuesNode = jobNode.SelectSingleNode("//Job[@Name='" + processorJob.JobName + "']/Queues");
			if (queuesNode != null)
				LoadJobQueues(processorJob, queuesNode);
			//Load the steps
			XmlNode stepsNode = jobNode.SelectSingleNode("//Job[@Name='" + processorJob.JobName + "']/Steps");
			if (stepsNode != null)
				LoadJobSteps(processorJob, stepsNode);
			else // For backwards compatibility
				LoadJobSteps(processorJob, jobNode);
		}

		/// <summary>
		/// Load the Job's Queues
		/// </summary>
		/// <param name="processorJob">The ProcessorJob to load to</param>
		/// <param name="queuesNode">The XmlNode to load from</param>
		private static void LoadJobQueues(ProcessorJob processorJob, XmlNode queuesNode)
		{
			foreach (XmlNode queueNode in queuesNode.ChildNodes)
			{
				ProcessorQueue processorQueue = new ProcessorQueue();

				if (!string.IsNullOrEmpty(queueNode.Attributes["MessageQueue"].Value)) //Required
					processorQueue.MessageQueue = queueNode.Attributes["MessageQueue"].Value;

				if (!string.IsNullOrEmpty(queueNode.Attributes["ErrorQueue"].Value)) //Required
					processorQueue.ErrorQueue = queueNode.Attributes["ErrorQueue"].Value;

				if (!string.IsNullOrEmpty(queueNode.Attributes["PoisonQueue"].Value)) //Required
					processorQueue.PoisonQueue = queueNode.Attributes["PoisonQueue"].Value;

				if (!string.IsNullOrEmpty(queueNode.Attributes["CompletedQueue"].Value)) //Required
					processorQueue.CompletedQueue = queueNode.Attributes["CompletedQueue"].Value;
				
				if (!string.IsNullOrEmpty(queueNode.Attributes["MessageQueueType"].Value)) //Required
					processorQueue.MessageQueueType = (MessageQueueType)Enum.Parse(typeof(MessageQueueType), queueNode.Attributes["MessageQueueType"].Value, true);

				processorJob.ProcessorQueues.Add(processorQueue);
			}
		}

		/// <summary>
		/// Load the Job's steps
		/// </summary>
		/// <param name="processorJob">The ProcessorJob to load to</param>
		/// <param name="queuesNode">The XmlNode to load from</param>
		private static void LoadJobSteps(ProcessorJob processorJob, XmlNode stepsNode)
		{
			foreach (XmlNode stepNode in stepsNode.ChildNodes)
			{
				ProcessorStep workFlowStep = new ProcessorStep();

				if (!string.IsNullOrEmpty(stepNode.Attributes["Name"].Value)) //Required
					workFlowStep.StepName = stepNode.Attributes["Name"].Value;
				if (!string.IsNullOrEmpty(stepNode.Attributes["Group"].Value)) //Required
					workFlowStep.Group = stepNode.Attributes["Group"].Value;
				if (!string.IsNullOrEmpty(stepNode.Attributes["InvokeClass"].Value)) //Required
					workFlowStep.InvokeClass = stepNode.Attributes["InvokeClass"].Value;
				if (stepNode.Attributes["OnError"] != null && !string.IsNullOrEmpty(stepNode.Attributes["OnError"].Value))
					workFlowStep.OnError = (OnFrameworkStepError)Enum.Parse(typeof(OnFrameworkStepError), stepNode.Attributes["OnError"].Value, true);
				if (stepNode.Attributes["RetryTimes"] != null && !string.IsNullOrEmpty(stepNode.Attributes["RetryTimes"].Value))
				{
					int retryTimes = 0;
					int.TryParse(stepNode.Attributes["RetryTimes"].Value, out retryTimes);
					workFlowStep.RetryTimes = retryTimes;
				}
				if (stepNode.Attributes["WaitBetweenRetriesMilliseconds"] != null && !string.IsNullOrEmpty(stepNode.Attributes["WaitBetweenRetriesMilliseconds"].Value))
				{
					int waitBetweenRetriesMilliseconds = 0;
					int.TryParse(stepNode.Attributes["WaitBetweenRetriesMilliseconds"].Value, out waitBetweenRetriesMilliseconds);
					workFlowStep.WaitBetweenRetriesMilliseconds = waitBetweenRetriesMilliseconds;
				}
				if (stepNode.Attributes["RunMode"] != null && !string.IsNullOrEmpty(stepNode.Attributes["RunMode"].Value))
					workFlowStep.RunMode = (FrameworkStepRunMode)Enum.Parse(typeof(FrameworkStepRunMode), stepNode.Attributes["RunMode"].Value, true);
				if (stepNode.Attributes["DependsOn"] != null && !string.IsNullOrEmpty(stepNode.Attributes["DependsOn"].Value))
					workFlowStep.DependsOn = stepNode.Attributes["DependsOn"].Value;
				if (stepNode.Attributes["DependsOnGroup"] != null && !string.IsNullOrEmpty(stepNode.Attributes["DependsOnGroup"].Value))
					workFlowStep.DependsOnGroup = stepNode.Attributes["DependsOnGroup"].Value;
				if (stepNode.Attributes["WaitForDependsOnMilliseconds"] != null && !string.IsNullOrEmpty(stepNode.Attributes["WaitForDependsOnMilliseconds"].Value))
				{
					int waitForDependsOnMilliseconds = int.MaxValue;
					if (!string.IsNullOrEmpty(stepNode.Attributes["WaitForDependsOnMilliseconds"].Value))
						int.TryParse(stepNode.Attributes["WaitForDependsOnMilliseconds"].Value, out waitForDependsOnMilliseconds);
					workFlowStep.WaitForDependsOnMilliseconds = waitForDependsOnMilliseconds;
				}
				if (stepNode.Attributes["RunAsDomain"] != null && !string.IsNullOrEmpty(stepNode.Attributes["RunAsDomain"].Value))
					workFlowStep.RunAsDomain = Utils.GetDecryptedString(stepNode.Attributes["RunAsDomain"].Value);
				if (stepNode.Attributes["RunAsUser"] != null && !string.IsNullOrEmpty(stepNode.Attributes["RunAsUser"].Value))
					workFlowStep.RunAsUser = Utils.GetDecryptedString(stepNode.Attributes["RunAsUser"].Value);
				if (stepNode.Attributes["RunAsPassword"] != null && !string.IsNullOrEmpty(stepNode.Attributes["RunAsPassword"].Value))
					workFlowStep.RunAsPassword = Utils.GetDecryptedString(stepNode.Attributes["RunAsPassword"].Value);
				processorJob.WorkFlowSteps.Add(workFlowStep);
			}
		}

	}
}
