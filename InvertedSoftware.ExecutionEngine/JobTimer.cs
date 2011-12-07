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
using System.Threading;
using System.Timers;

namespace InvertedSoftware.WorkflowEngine
{
	public class JobTimer
	{
		private System.Timers.Timer jobTimer;
		private Thread jobThread;

		public JobTimer(int interval, Thread jobThread)
		{
			this.jobThread = jobThread;
			jobTimer = new System.Timers.Timer(interval);
			jobTimer.AutoReset = false;
			jobTimer.Elapsed += new ElapsedEventHandler(OnTimedEvent);
			jobTimer.Enabled = true;
		}

		public void StopTimer()
		{
			if (jobTimer != null)
			{
				jobTimer.Enabled = false;
				jobTimer.Dispose();
			}
		}

		private void OnTimedEvent(object source, ElapsedEventArgs e)
		{
			jobThread.Abort();
		}
	}
}
