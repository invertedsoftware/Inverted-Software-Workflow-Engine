// Copyright (c) Inverted Software. All rights reserved.

using System.IO;
using System.Windows;
using InvertedSoftware.WorkflowEngine.Config;
using InvertedSoftware.WorkflowEngine.Messages;
using InvertedSoftware.WorkflowEngine.Queue.InMemory;
using InvertedSoftware.WorkflowEngine.Queue.Serialization;
using InvertedSoftware.WorkflowEngine.Steps;
using Microsoft.Extensions.Configuration;

namespace InvertedSoftware.WorkflowEngine.Example;

/// <summary>
/// WPF demo. Uses the in-memory queue provider so the demo runs without any
/// external broker — swap for RabbitMQ / Kafka / Azure Service Bus by changing
/// the provider construction below.
/// </summary>
public partial class MainWindow : Window
{
    private readonly WorkflowEngineHost _host;
    private readonly Processor _processor;
    private const string JobName = "ExampleJob";

    public MainWindow()
    {
        InitializeComponent();

        var configRoot = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        var options = configRoot.GetSection("WorkflowEngine").Get<EngineOptions>() ?? new EngineOptions();

        var stepFactory = new TypeNameStepFactory()
            .Register<CopyFiles>("InvertedSoftware.WorkflowEngine.Steps.CopyFiles", () => new CopyFiles())
            .Register<RenameFiles>("InvertedSoftware.WorkflowEngine.Steps.RenameFiles", () => new RenameFiles());

        _host = new WorkflowEngineHost(
            queueProvider: new InMemoryQueueProvider(),
            serializer: new JsonMessageSerializer(),
            stepFactory: stepFactory,
            options: options);

        _processor = _host.CreateProcessor();
        LoadWorkflow();
    }

    private void LoadWorkflow()
    {
        WorkflowTextBox.Text = File.ReadAllText(_host.Options.FrameworkConfigLocation);
    }

    private void button1_Click(object sender, RoutedEventArgs e)
    {
        Task.Run(async () =>
        {
            try
            {
                await _processor.StartFrameworkAsync(JobName);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => MessageBox.Show("Framework Error: " + ex.Message));
            }
        });
    }

    private async void button2_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var message = new ExampleMessage
            {
                CopyFilesFrom = @"C:\FrameworkTest\Source\",
                CopyFilesTo = @"C:\FrameworkTest\Destination\",
            };
            await FrameworkManager.AddFrameworkJobAsync(JobName, message);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString());
        }
    }

    private async void button3_Click(object sender, RoutedEventArgs e)
    {
        await _processor.StopFrameworkAsync(isSoftExit: true);
    }
}
