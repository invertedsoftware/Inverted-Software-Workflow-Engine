// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND,
// EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE IMPLIED
// WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A PARTICULAR PURPOSE.
//
// Copyright (C) Inverted Software(TM). All rights reserved.
//
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Common.Exceptions;

namespace InvertedSoftware.WorkflowEngine.Steps
{
	internal class RenameFiles : IStep
	{
		/// <summary>
		/// The method executed by the framework
		/// </summary>
		/// <param name="message"></param>
		public void RunStep(IWorkflowMessage message)
		{
			ExampleMessage myMessage = message as ExampleMessage;
			if (myMessage == null)
				throw new WorkflowStepException("IWorkflowMessage is of the wrong type");

			try
			{
				Parallel.ForEach<string>(Directory.EnumerateFiles(myMessage.CopyFilesTo, "*.jpg"), f =>
				{
                    try
                    {
                        File.Move(f, f + "." + Guid.NewGuid());
                    }
                    catch (Exception) { }
				});
			}
			catch (Exception e)
			{
				throw new WorkflowStepException(e.Message, e);
			}
		}

		/// <summary>
		/// Dispose
		/// </summary>
		public void Dispose()
		{
			GC.SuppressFinalize(this);
		}
	}
}
