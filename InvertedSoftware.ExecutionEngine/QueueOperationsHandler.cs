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
using System.ServiceModel;
using System.Transactions;
using System.Messaging;

using InvertedSoftware.WorkflowEngine.DataObjects;
using InvertedSoftware.WorkflowEngine.Messages;

namespace InvertedSoftware.WorkflowEngine
{
	/// <summary>
	/// Handle queue operations
	/// </summary>
	public class QueueOperationsHandler
	{
		public static void HandleError(MsmqPoisonMessageException error)
		{
			ProcessorQueue processorQueue = (ProcessorQueue)error.Data["processorQueue"];
			MessageQueue poisonQueue = new System.Messaging.MessageQueue(processorQueue.PoisonQueue);
			MessageQueue errorQueue = new System.Messaging.MessageQueue(processorQueue.ErrorQueue);

			using (TransactionScope txScope = new TransactionScope(TransactionScopeOption.RequiresNew))
			{
				try
				{
					// Send the message to the poison and error message queues.
					poisonQueue.Send((IWorkflowMessage)error.Data["message"], MessageQueueTransactionType.Automatic);
					errorQueue.Send((WorkflowErrorMessage)error.Data["errorMessage"], MessageQueueTransactionType.Automatic);
					txScope.Complete();
				}
				catch (InvalidOperationException)
				{

				}
				finally
				{
					poisonQueue.Dispose();
					errorQueue.Dispose();
				}
			}
		}

		public static void HandleComplete(ProcessorQueue processorQueue, IWorkflowMessage message)
		{
			MessageQueue completedQueue = new System.Messaging.MessageQueue(processorQueue.CompletedQueue);

			using (TransactionScope txScope = new TransactionScope(TransactionScopeOption.RequiresNew))
			{
				try
				{
					completedQueue.Send(message, MessageQueueTransactionType.Automatic);
					txScope.Complete();
				}
				catch (InvalidOperationException)
				{

				}
				finally
				{
					completedQueue.Dispose();
				}
			}
		}
	}
}
