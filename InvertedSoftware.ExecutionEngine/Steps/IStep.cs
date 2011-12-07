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

using InvertedSoftware.WorkflowEngine.Messages;

namespace InvertedSoftware.WorkflowEngine.Steps
{
	/// <summary>
	/// The base interface for all framework steps
	/// </summary>
	public interface IStep : IDisposable
	{
		/// <summary>
		/// Executes this step
		/// </summary>
		/// <param name="message">The message sent to this step from the framework</param>
		void RunStep(IWorkflowMessage message);
	}
}
