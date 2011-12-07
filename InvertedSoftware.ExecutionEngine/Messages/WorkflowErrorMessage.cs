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
	[Serializable]
	public class WorkflowErrorMessage
	{
		public string JobName { get; set; }
		public string StepName { get; set; }
		public string ExceptionMessage { get; set; }
	}
}
