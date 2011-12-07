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
	/// <summary>
	/// Holds a Queue used for Queue Redundancy
	/// </summary>
	[Serializable()]
	public class ProcessorQueue
	{
		private string messageQueue = string.Empty;
		/// <summary>
		/// The location of the private Transactional Queue used to store the job
		/// </summary>
		public string MessageQueue
		{
			get
			{
				return messageQueue;
			}
			set
			{
				if (value != messageQueue)
					OnProcessorQueueChanged(new ProcessorQueueChangedEventArgs() { NewMessageQueue = value, OldMessageQueue = messageQueue });
				messageQueue = value;
			}
		}

		/// <summary>
		/// The location of the private Transactional Queue used for job errors
		/// </summary>
		public string ErrorQueue { get; set; }
		/// <summary>
		/// The location of the private Transactional Queue used as poison Queue
		/// </summary>
		public string PoisonQueue { get; set; }
		/// <summary>
		/// The transactional queue containing the completed job message
		/// </summary>
		public string CompletedQueue { get; set; }
		/// <summary>
		/// The type of Queue
		/// </summary>
		public MessageQueueType MessageQueueType { get; set; }

		#region Events
		public delegate void ProcessorQueueEventHandler(object sender, ProcessorQueueChangedEventArgs e);
		public event ProcessorQueueEventHandler ProcessorQueueChanged;

		protected virtual void OnProcessorQueueChanged(ProcessorQueueChangedEventArgs e)
		{
			if (ProcessorQueueChanged != null)
				ProcessorQueueChanged(this, e);
		}
		#endregion
	}
}
