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

namespace InvertedSoftware.WorkflowEngine.DataObjects
{
	public enum OnFrameworkStepError
	{
		RetryJob,
		Skip,
		RetryStep,
		Exit
	}

	public enum FrameworkStepRunMode
	{
		STA,
		MTA
	}

	public enum FrameworkStepRunStatus
	{
		Loaded,
		Waiting,
		Complete,
		CompleteWithErrors
	}

	/// <summary>
	/// Holds a single framework step
	/// </summary>
	[Serializable()]
	public class ProcessorStep
	{
		#region Config
		/// <summary>
		/// The name of the step
		/// </summary>
		public string StepName { get; set; }
		/// <summary>
		/// The name of the group this step belongs to
		/// </summary>
		public string Group { get; set; }
		/// <summary>
		/// Class to invoke for execution
		/// </summary>
		public string InvokeClass { get; set; }
		/// <summary>
		/// Operation to take on error: RetryJob|Skip|RetryStep|Exit. When run mode is MTA, only RetryStep can be applied
		/// </summary>
		public OnFrameworkStepError OnError { get; set; }
		/// <summary>
		/// Number of times to retry the step when RetryStep is set or job when RetryJob is set
		/// </summary>
		public int RetryTimes { get; set; }
		/// <summary>
		/// How much time to wait before retrying a step or a job. default is 0
		/// </summary>
		public int WaitBetweenRetriesMilliseconds { get; set; }
		/// <summary>
		/// Sync or Async mode to execute the step STA|MTA
		/// </summary>
		public FrameworkStepRunMode RunMode { get; set; }
		/// <summary>
		/// Comma delimited string of all the steps that need to be completed before this step can run
		/// </summary>
		public string DependsOn { get; set; }
		/// <summary>
		/// Comma delimited string of all the groups that need to be completed before this step can run
		/// </summary>
		public string DependsOnGroup { get; set; }
		/// <summary>
		/// When DependsOn is set, indicates the maximum amount of time to wait
		/// </summary>
		public int WaitForDependsOnMilliseconds { get; set; }
		/// <summary>
		/// Used for Impersonation. Executes a step as a user. Only works in STA mode
		/// </summary>
		public string RunAsDomain { get; set; }
		/// <summary>
		/// Used for Impersonation. Executes a step as a user. Only works in STA mode
		/// </summary>
		public string RunAsUser { get; set; }
		/// <summary>
		/// Used for Impersonation. Executes a step as a user. Only works in STA mode
		/// </summary>
		public string RunAsPassword { get; set; }

		#endregion

		#region Runtime
		/// <summary>
		/// The run status Loaded|Waiting|Complete|CompleteWithErrors
		/// </summary>
		public FrameworkStepRunStatus RunStatus { get; set; }
		/// <summary>
		/// The time in miliseconds this step was in the current RunStatus.
		/// Used for Waiting for other steps to finish
		/// </summary>
		public int RunStatusTime { get; set; }
		#endregion

		#region Log
		/// <summary>
		///  The step ID in table JobStep
		/// </summary>
		public int FrameworkJobStepID { get; set; }
		public int FrameworkJobID { get; set; }
		public DateTime? CreatedDate { get; set; }
		public DateTime? StartDate { get; set; }
		public DateTime? EndDate { get; set; }
		public string ExitMessage { get; set; }
		public bool Active { get; set; }
		#endregion

		public ProcessorStep()
		{
			StepName = string.Empty;
			Group = string.Empty;
			InvokeClass = string.Empty;
			OnError = OnFrameworkStepError.Skip;
			RetryTimes = 0;
			RunMode = FrameworkStepRunMode.STA;
			DependsOn = string.Empty;
			DependsOnGroup = string.Empty;
			WaitForDependsOnMilliseconds = int.MaxValue;
			RunStatus = FrameworkStepRunStatus.Loaded;
			RunStatusTime = 0;
			FrameworkJobStepID = -1;
			FrameworkJobID = -1;
			CreatedDate = null;
			StartDate = null;
			EndDate = null;
			Active = true;
		}
	}
}
