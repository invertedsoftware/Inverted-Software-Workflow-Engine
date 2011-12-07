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

namespace InvertedSoftware.WorkflowEngine.Messages
{
	/// <summary>
	/// The interface for framework messages,
	/// Any message dropped into a framework Queue must implement this interface
	/// </summary>
	public interface IWorkflowMessage
	{
		/// <summary>
		/// The database Job ID
		/// </summary>
		int JobID { get; set; }
	}
}
