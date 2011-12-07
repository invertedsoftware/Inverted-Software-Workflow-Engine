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
	public class ProcessorQueueChangedEventArgs : EventArgs
	{
		/// <summary>
		/// The location of the private Transactional Queue used to store the job
		/// </summary>
		public string NewMessageQueue { get; set; }
		/// <summary>
		/// The location old private Transactional Queue used to store the job
		/// </summary>
		public string OldMessageQueue { get; set; }
	}
}
