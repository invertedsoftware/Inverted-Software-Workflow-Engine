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
using System.Reflection;

namespace InvertedSoftware.WorkflowEngine.Steps
{
	/// <summary>
	/// The factory in charge of creating steps
	/// </summary>
	public static class StepFactory
	{
		/// <summary>
		/// Create a new step
		/// </summary>
		/// <param name="stepName">The name of the step class to create</param>
		/// <returns></returns>
		public static IStep GetStep(string stepName)
		{
			return (IStep)Assembly.Load(Assembly.GetExecutingAssembly().FullName).CreateInstance(stepName);
		}
	}
}
