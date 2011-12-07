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
using System.Runtime.Serialization.Formatters.Binary;
using System.IO;

namespace InvertedSoftware.WorkflowEngine.DataObjects
{
	/// <summary>
	/// The type of Queue
	/// </summary>
	public enum MessageQueueType
	{
		Transactional,
		NonTransactional
	}

	/// <summary>
	/// The operation to execute on the Queue
	/// </summary>
	public enum QueueOperationType
	{
		Pickup,
		Delivery
	}
	/// <summary>
	/// Holds a single framework job
	/// </summary>
	[Serializable()]
	public class ProcessorJob: ICloneable
	{
		#region Config
		/// <summary>
		/// The name of the job
		/// </summary>
		public string JobName { get; set; }
		/// <summary>
		/// The Message class in the Queue
		/// </summary>
		public string MessageClass { get; set; }
		/// <summary>
		/// The location of the private Transactional Queue used to store the job
		/// </summary>
		public string MessageQueue { get; set; }
		/// <summary>
		/// The location of the private Transactional Queue used for job errors
		/// </summary>
		public string ErrorQueue { get; set; }
		/// <summary>
		/// PoisonQueue
		/// </summary>
		public string PoisonQueue { get; set; }
		/// <summary>
		/// The transactional queue containing the completed job message
		/// </summary>
		public string CompletedQueue { get; set; }
		/// <summary>
		/// When set to true sends the original message to the CompletedQueue on successful job compilation
		/// </summary>
		public bool NotifyComplete { get; set; }
		/// <summary>
		/// The Maximum time a job can run. The default is one hour
		/// </summary>
		public int MaxRunTimeMilliseconds { get; set; }
		/// <summary>
		/// The type of Queue
		/// </summary>
		public MessageQueueType MessageQueueType { get; set; }
		/// <summary>
		/// The list of Queues available for the framework
		/// </summary>
		public List<ProcessorQueue> ProcessorQueues { get; set; }
		#endregion

		#region Log
		/// <summary>
		/// The ID of the job in the database
		/// </summary>
		public int FrameworkJobID { get; set; }
		/// <summary>
		/// General description of this job
		/// </summary>
		public string Description { get; set; }
		/// <summary>
		/// XML Shaped message object to be stored in the database
		/// </summary>
		public string MessageData { get; set; }
		/// <summary>
		/// The User ID that created this job
		/// </summary>
		public int CreatedBy { get; set; }
		/// <summary>
		/// Indicates when the job was created and dropped int the Queue
		/// </summary>
		public DateTime? CreatedDate { get; set; }
		/// <summary>
		/// Indicates when the job was picked up from the Queue and started running
		/// </summary>
		public DateTime? StartDate { get; set; }
		/// <summary>
		/// Indicates when the job successfully finished running
		/// </summary>
		public DateTime? EndDate { get; set; }
		/// <summary>
		/// The last message to be set by the job or one of its steps
		/// </summary>
		public string ExitMessage { get; set; }
		/// <summary>
		/// Is this an active job
		/// </summary>
		public bool Active { get; set; }
		#endregion

		/// <summary>
		/// List of steps in this job
		/// </summary>
		public List<ProcessorStep> WorkFlowSteps { get; set; }

		public ProcessorJob()
		{
			JobName = string.Empty;
			MessageClass = string.Empty;
			MessageQueue = string.Empty;
			ErrorQueue = string.Empty;
			PoisonQueue = string.Empty;
			CompletedQueue = string.Empty;
			NotifyComplete = false;
			MaxRunTimeMilliseconds = 3600000;
			FrameworkJobID = -1;
			Description = string.Empty;
			MessageData = string.Empty;
			CreatedBy = -1;
			CreatedDate = null;
			StartDate = null;
			EndDate = null;
			ExitMessage = string.Empty;
			Active = true;
			ProcessorQueues = new List<ProcessorQueue>();
			WorkFlowSteps = new List<ProcessorStep>();
		}

		#region ICloneable Members
		public object Clone()
		{
			object copy;
			using (MemoryStream ms = new MemoryStream())
			{
				BinaryFormatter bf = new BinaryFormatter();
				bf.Serialize(ms, this);
				ms.Flush();
				ms.Position = 0;
				copy = bf.Deserialize(ms);
			}
			return copy;
		}
		#endregion

	}
}
