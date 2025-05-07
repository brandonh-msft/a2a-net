// Copyright � 2025-Present the a2a-net Authors
//
// Licensed under the Apache License, Version 2.0 (the "License"),
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using A2A.Events;
using A2A.Samples.SemanticKernel.Client;

using Microsoft.VisualStudio.Threading;

using System.Text;

var configuration = new ConfigurationBuilder()
    .AddCommandLine(args)
    .AddEnvironmentVariables()
    .AddUserSecrets(typeof(ApplicationOptions).Assembly)
    .AddJsonFile("appsettings.json", true)
    .Build();
var applicationOptions = new ApplicationOptions();
configuration.Bind(applicationOptions);

// Allow `--streaming` to be a flag or a value
if (applicationOptions.Streaming is false)
{
    var streamingArg = args.Select((v, i) => (i, v)).FirstOrDefault(i => i.v.Contains("streaming", StringComparison.OrdinalIgnoreCase));
    if (streamingArg != default)
    {
        applicationOptions.Streaming = streamingArg.i + 1 >= args.Length || bool.Parse(args[streamingArg.i + 1]);
    }
}

ArgumentNullException.ThrowIfNull(applicationOptions.Server);

var services = new ServiceCollection();
services.ConfigureHttpClientDefaults(b => b.ConfigureHttpClient(c => c.Timeout = TimeSpan.FromDays(1)));
services.AddA2AProtocolHttpClient(options => options.Endpoint = applicationOptions.Server);

var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<IA2AProtocolClient>();
var cancellationSource = new CancellationTokenSource();
AnsiConsole.Write(new FigletText("A2A Protocol Chat").Color(Color.Blue));
AnsiConsole.MarkupLine("[gray]Type your prompts below. Press [bold]Ctrl+C[/] to exit.[/]\n");
var responseSoFar = new StringBuilder();
var session = Guid.NewGuid().ToString("N");

CancellationTokenSource spinnerCanceller = new();
System.Threading.Tasks.Task spinner;
void cancelSpinner()
{
    spinnerCanceller.Cancel();
    try
    {
        spinner.Wait(spinnerCanceller.Token);
    }
    catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
    {
        // Ignore cancellation exceptions
    }
}

void spinStopThen(Action runThis)
{
    spinnerCanceller.Cancel();
    runThis();
}

while (true)
{
    var prompt = AnsiConsole.Ask<string>("[bold blue]User>[/]");
    if (string.IsNullOrWhiteSpace(prompt))
    {
        AnsiConsole.MarkupLine("[yellow]⚠️ Please enter a prompt.[/]");
        continue;
    }

    if (prompt is "/agent" or "/agents")
    {
        await printAgentCardsAsync();
        continue;
    }

    var filePath = AnsiConsole.Ask<string>("[blue]File path (optional, <enter> to skip)>[/]", string.Empty).TrimStart('"').TrimEnd('"');
    string? filename = !string.IsNullOrWhiteSpace(filePath) ? Path.GetFileName(filePath) : null;
    var fileBytes = !string.IsNullOrWhiteSpace(filePath) ? System.IO.File.ReadAllBytes(filePath) : null;

    try
    {
        spinner = AnsiConsole.Status()
            .Spinner(Spinner.Known.SquareCorners)
            .SpinnerStyle(Style.Parse("green"))
            .StartAsync("Communicating with Agent...", ctx => System.Threading.Tasks.Task.Delay(Timeout.Infinite, spinnerCanceller.Token));

        var parts = new List<Part>() { new TextPart(prompt) };
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            parts.Add(new FilePart { File = new() { Bytes = Convert.ToBase64String(fileBytes!), Name = filename } });
        }

        var taskParams = new TaskSendParameters
        {
            SessionId = session,
            Message = new()
            {
                Role = MessageRole.User,
                Parts = [.. parts]
            }
        };

        if (applicationOptions.Streaming is true)
        {
            var request = new SendTaskStreamingRequest { Params = taskParams };

            bool first = true, firstArtifact = true;
            try
            {
                await foreach (var response in client.SendTaskStreamingAsync(request, cancellationSource.Token))
                {
                    if (first)
                    {
                        cancelSpinner();
                        first = false;
                    }

                    if (response.Error is not null)
                    {
                        spinStopThen(() => AnsiConsole.MarkupLineInterpolated($"[red]❌ Error: {response.Error.Message}[/]"));
                        continue;
                    }

                    if (response.Result is TaskArtifactUpdateEvent artifactEvent)
                    {
                        cancelSpinner();

                        if (firstArtifact)
                        {
                            firstArtifact = false;
                            AnsiConsole.Markup($"[bold green]Agent>[/] ");
                        }

                        if (artifactEvent.Artifact.Append is false)
                        {
                            Console.WriteLine();
                        }

                        await printArtifactAsync(artifactEvent.Artifact);

                        if (artifactEvent.Artifact.LastChunk is true)
                        {
                            Console.WriteLine();
                        }
                    }
                    else if (response.Result is TaskStatusUpdateEvent evt)
                    {
                        var msg = evt.Status.Message?.ToText() ?? string.Empty;

                        if (msg.Contains("ToolCalls:InProgress") is true && spinnerCanceller.IsCancellationRequested)
                        {
                            spinnerCanceller = new CancellationTokenSource();
                            spinner = AnsiConsole.Status()
                                .Spinner(Spinner.Known.SquareCorners)
                                .SpinnerStyle(Style.Parse("grey58"))
                                .StartAsync("[grey23]Running tool...[/]", ctx => System.Threading.Tasks.Task.Delay(Timeout.Infinite, spinnerCanceller.Token));

                            continue;
                        }
                        else if (msg.Contains("ToolsCalls:Completed") is true && !spinnerCanceller.IsCancellationRequested)
                        {
                            cancelSpinner();
                        }
                        else if (!spinnerCanceller.IsCancellationRequested)
                        {
                            continue;
                        }

                        AnsiConsole.MarkupInterpolated($"[grey23]{msg}[/]");
                        if (evt.Final is true)
                        {
                            Console.WriteLine();
                        }
                    }
                    else
                    {
                        spinStopThen(() => AnsiConsole.MarkupLineInterpolated($"[red]Unknown event type: {response.Result?.GetType().Name}[/]"));
                    }
                }
            }
            finally
            {
                spinStopThen(() => Console.WriteLine());
            }
        }
        else
        {
            var request = new SendTaskRequest { Params = taskParams };

            var task = await client.SendTaskAsync(request);
            if (task.Error is not null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]❌ Error: {task.Error.Message}[/]");
                continue;
            }

            if (task.Result?.Artifacts?.Count is not null and not 0)
            {
                AnsiConsole.Markup($"[bold green]Agent>[/] ");
                foreach (var a in task.Result?.Artifacts ?? [])
                {
                    await printArtifactAsync(a);
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]❌ Error: No artifacts found in response.[/]");
            }
        }
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLineInterpolated($"[red]❌ Error: {ex.Message}[/]");
    }
}

static async System.Threading.Tasks.Task printArtifactAsync(Artifact artifact)
{
    foreach (var p in artifact.Parts ?? [])
    {
        if (p is TextPart t)
        {
            AnsiConsole.Markup(p.ToText()?.EscapeMarkup() ?? string.Empty);
        }
        else if (p is FilePart f)
        {
            AnsiConsole.MarkupLineInterpolated($"[darkgreen]File: {f.File.Name}[/]");
            if (f.File.Uri is not null)
            {
                AnsiConsole.MarkupLineInterpolated($"[darkgreen]URI: {f.File.Uri}[/]");
            }
            else if (f.File.Bytes is not null)
            {
                var filename = Path.Combine(Path.GetTempPath(), f.File.Name!);
                await System.IO.File.WriteAllBytesAsync(filename, Convert.FromBase64String(f.File.Bytes!));
                AnsiConsole.MarkupLineInterpolated($"[darkgreen]Downloaded to: {filename}[/]");
            }
        }
        else if (p is DataPart d)
        {
            AnsiConsole.MarkupLineInterpolated($"[darkgreen]Data: {(d.Type is null ? "Unknown" : d.Type)}[/]");
            foreach (var i in d.Metadata ?? [])
            {
                AnsiConsole.MarkupLineInterpolated($"[darkgreen]{i.Key}: {i.Value}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Unknown part type: {p.Type}[/]");
        }
    }
}