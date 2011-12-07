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
using System.Configuration;

namespace InvertedSoftware.WorkflowEngine.Config
{
	public class EngineConfiguration
	{
		/// <summary>
		/// Maximum threads to run framework jobs
		/// </summary>
		public static int FrameworkMaxThreads
		{
			get
			{
				return Convert.ToInt32(ConfigurationManager.AppSettings["FrameworkMaxThreads"]);
			}
		}

		/// <summary>
		/// Indicate whether to use pipelined execution on multi core servers
		/// </summary>
		public static bool UsePipelinedOnMulticore
		{
			get
			{
				return Convert.ToBoolean(ConfigurationManager.AppSettings["UsePipelinedOnMulticore"]);
			}
		}

		/// <summary>
		/// The location for the framework workflow file
		/// </summary>
		public static string FrameworkConfigLocation
		{
			get
			{
				return ConfigurationManager.AppSettings["FrameworkConfigLocation"];
			}
		}
	}
}
