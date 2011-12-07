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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.IO;
using System.Threading;

using InvertedSoftware.WorkflowEngine.Messages;
using System.Threading.Tasks;

namespace InvertedSoftware.WorkflowEngine.Example
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
	{
		public Processor frameworkProcessor;
		public bool isSoftStop;
		public string JobName = "ExampleJob";

		public MainWindow()
		{
			InitializeComponent();
			LoadWorkflow();
			frameworkProcessor = new Processor();
			isSoftStop = false;
		}

		private void LoadWorkflow()
		{
			WorkflowTextBox.Text = File.ReadAllText(WorkflowEngine.Config.EngineConfiguration.FrameworkConfigLocation);
		}

		/// <summary>
		/// Start the framework
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void button1_Click(object sender, RoutedEventArgs e)
		{
			Task.Factory.StartNew(() =>
			{
				try
				{
					frameworkProcessor.StartFramework(JobName);
				}
				catch (Exception ex)
				{
					MessageBox.Show("Framework Error." + ex.Message);
				}
			});
		}

		/// <summary>
		/// Drop a request message in the queue
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void button2_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				ExampleMessage message = new ExampleMessage()
					{
						CopyFilesFrom = @"C:\FrameworkTest\Source\",
						CopyFilesTo = @"C:\FrameworkTest\Destination\"
					};
				FrameworkManager.AddFrameworkJob(JobName, message);
			}
			catch (Exception ex)
			{
				MessageBox.Show(ex.ToString());
			}
		}

		/// <summary>
		/// Stop the framework
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void button3_Click(object sender, RoutedEventArgs e)
		{
			frameworkProcessor.StopFramework(isSoftStop);
		}
	}
}
