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

namespace InvertedSoftware.WorkflowEngine.Common.Exceptions
{
	public class WorkflowException : Exception
	{
		public WorkflowException()
			: base()
		{
			
		}

		public WorkflowException(string s)
			: base(s)
		{
			
		}

		public WorkflowException(string s, Exception innerException)
			: base(s, innerException)
		{
			
		}
	}
}
