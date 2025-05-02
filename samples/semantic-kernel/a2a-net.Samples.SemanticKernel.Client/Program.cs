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
while (true)
{
    var prompt = AnsiConsole.Ask<string>("[bold blue]User>[/]");
    if (string.IsNullOrWhiteSpace(prompt))
    {
        AnsiConsole.MarkupLine("[yellow]⚠️ Please enter a prompt.[/]");
        continue;
    }

    var request = new SendTaskStreamingRequest
    {
        Params = new()
        {
            SessionId = session,
            Message = new()
            {
                Role = MessageRole.User,
                Parts = [new TextPart(prompt)]
            }
        }
    };

    try
    {
        CancellationTokenSource cts = new();
        AnsiConsole.Status()
            .Spinner(Spinner.Known.SquareCorners)
            .SpinnerStyle(Style.Parse("green"))
            .StartAsync("Communicating with Agent... ", ctx => System.Threading.Tasks.Task.Delay(Timeout.Infinite, cts.Token));

        bool first = true;
        await foreach (var response in client.SendTaskStreamingAsync(request, cancellationSource.Token))
        {
            if (response.Error is not null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]❌ Error: {response.Error.Message}[/]");
                continue;
            }

            cts.Cancel();

            if (response.Result is TaskArtifactUpdateEvent artifact)
            {
                if (first)
                {
                    first = false;
                    AnsiConsole.Markup($"[bold green]Agent>[/] ");
                }

                if (artifact.Artifact.Append is false)
                {
                    Console.WriteLine();
                }

                foreach (var p in artifact.Artifact.Parts ?? [])
                {
                    if (p is TextPart t)
                    {
                        AnsiConsole.Markup(p.ToText()?.EscapeMarkup() ?? string.Empty);
                    }
                    else if (p is FilePart f)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[green]File: {f.File.Name}[/]");
                        if (f.File.Uri is not null)
                        {
                            AnsiConsole.MarkupLineInterpolated($"[green]URI: {f.File.Uri}[/]");
                        }
                        else if (f.File.Bytes is not null)
                        {
                            var filename = System.IO.Path.GetTempFileName();
                            await System.IO.File.WriteAllBytesAsync(filename, Convert.FromBase64String(f.File.Bytes!));
                            AnsiConsole.MarkupLineInterpolated($"[green]Downloaded to: {filename}[/]");
                        }
                    }
                    else if (p is DataPart d)
                    {
                        AnsiConsole.MarkupLineInterpolated($"[green]Data: {(d.Type is null ? "Unknown" : d.Type)}[/]");
                        foreach (var i in d.Metadata ?? [])
                        {
                            AnsiConsole.MarkupLineInterpolated($"[green]{i.Key}: {i.Value}[/]");
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLineInterpolated($"[red]Unknown part type: {p.Type}[/]");
                    }
                }

                if (artifact.Artifact.LastChunk is true)
                {
                    Console.WriteLine();
                }
            }
            else if (response.Result is TaskStatusUpdateEvent evt)
            {
                AnsiConsole.MarkupInterpolated($"[grey23]{evt.Status.Message?.ToText() ?? string.Empty}[/]");
                if (evt.Final is true)
                {
                    Console.WriteLine();
                }
            }
            else
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Unknown event type: {response.Result?.GetType().Name}[/]");
            }

            if (configuration["DOTNET_ENVIRONMENT"] is string s && s.Equals("Development", StringComparison.OrdinalIgnoreCase))
            {
                await System.Threading.Tasks.Task.Delay(50, cancellationSource.Token);
            }
        }

        Console.WriteLine();
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
