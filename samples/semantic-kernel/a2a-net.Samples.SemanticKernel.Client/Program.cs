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

using Azure.Core;
using Azure.Identity;

using Spectre.Console.Json;

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

var cred = new DefaultAzureCredential();
var token = await cred.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), default);

var services = new ServiceCollection();
services.ConfigureHttpClientDefaults(b => b.ConfigureHttpClient(c => c.Timeout = TimeSpan.FromDays(1)));
services.AddA2AProtocolHttpClient(options =>
{
    options.Endpoint = applicationOptions.Server;
    options.Authorization = () => ("Bearer", token.Token);
});

var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<IA2AProtocolClient>();

var agentCts = new CancellationTokenSource();
AnsiConsole.Write(new FigletText("A2A Protocol Chat").Color(Color.Blue));

var menu = new Grid()
    .AddColumn(new GridColumn().LeftAligned())
    .AddColumn(new GridColumn().Centered())
    .AddColumn(new GridColumn().LeftAligned())
    .AddRow([new Text(string.Empty), new Text("Menu", new(Color.Purple_2, decoration: Decoration.Underline | Decoration.Bold))])
    .AddRow("[bold yellow]/agent[/]", string.Empty, "Display Card for targeted Agent")
    .AddRow("[bold yellow]/registry | /agents[/]", string.Empty, "Display Cards for all available Agents in the workspace of the targeted Agent")
    .AddRow("[bold yellow]/reset[/]", string.Empty, "Resets the chat session (erases history)")
    .AddRow("[bold yellow]/exit | /quit | /q[/]", string.Empty, "Quit");
AnsiConsole.Write(menu);

Console.WriteLine();

AnsiConsole.MarkupLine("[gray]Type your prompts below.[/]\n");
var responseSoFar = new StringBuilder();
var session = Guid.NewGuid().ToString("N");

CancellationTokenSource spinnerCts = new();
System.Threading.Tasks.Task spinner = System.Threading.Tasks.Task.CompletedTask;
void cancelSpinner()
{
    spinnerCts.Cancel();
    try
    {
        spinner.Wait(spinnerCts.Token);
    }
    catch (Exception e) when (e is TaskCanceledException or OperationCanceledException)
    {
        // Ignore cancellation exceptions
    }
}

void spinStopThen(Action runThis)
{
    cancelSpinner();
    runThis();
}

while (true)
{
    cancelSpinner();
    var prompt = AnsiConsole.Ask<string>("[bold blue]User>[/]");
    if (string.IsNullOrWhiteSpace(prompt))
    {
        AnsiConsole.MarkupLine("[yellow]⚠️ Please enter a prompt.[/]");
        continue;
    }

    if (prompt is "/agent")
    {
        await printAgentCardAsync();
        continue;
    }
    else if (prompt is "/registry" or "/agents")
    {
        await printRegistryAsync(agentCts.Token);
        continue;
    }
    else if (prompt is "/reset")
    {
        session = Guid.NewGuid().ToString("N");
        responseSoFar = new();

        AnsiConsole.MarkupLine("[yellow]⚠️ Chat history reset.[/]");
        continue;
    }
    else if (prompt is "/exit" or ['/', 'q', ..] or ['/', 'x', ..])
    {
        break;
    }

    var filePath = AnsiConsole.Ask<string>("[blue]File path (optional, <enter> to skip)>[/]", string.Empty).TrimStart('"').TrimEnd('"');
    string? filename = !string.IsNullOrWhiteSpace(filePath) ? Path.GetFileName(filePath) : null;
    var fileBytes = !string.IsNullOrWhiteSpace(filePath) ? System.IO.File.ReadAllBytes(filePath) : null;

    spinnerCts = new();
    var agentCommSpinnerDef = AnsiConsole.Status()
                .Spinner(Spinner.Known.BouncingBar)
                .SpinnerStyle(Style.Parse("green"));

    try
    {
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
            spinner = agentCommSpinnerDef
                .StartAsync("Communicating with Agent...", ctx => System.Threading.Tasks.Task.Run(() => { while (true) ctx.Refresh(); }, spinnerCts.Token).WaitAsync(spinnerCts.Token));

            var request = new SendTaskStreamingRequest { Params = taskParams };

            bool firstArtifact = true;
            try
            {
                await foreach (var response in client.SendTaskStreamingAsync(request, agentCts.Token))
                {
                    if (response.Error is not null)
                    {
                        spinStopThen(() => AnsiConsole.MarkupLineInterpolated($"[red]❌ Error: {response.Error.Message}[/]"));
                        continue;
                    }

                    if (response.Result is TaskArtifactUpdateEvent artifactEvent)
                    {
                        if (firstArtifact)
                        {
                            cancelSpinner();
                            AnsiConsole.Markup($"[bold green]Agent>[/] ");
                            firstArtifact = false;
                        }
                        else if (artifactEvent.Artifact.Append is false)
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

                        if (msg.Contains("ToolCalls:InProgress") is true && spinnerCts.IsCancellationRequested)
                        {
                            cancelSpinner();
                            spinnerCts = new CancellationTokenSource();
                            spinner = AnsiConsole.Status()
                                .Spinner(Spinner.Known.SquareCorners)
                                .SpinnerStyle(Style.Parse("grey58"))
                                .StartAsync("[grey23]Running tool...[/]", ctx => System.Threading.Tasks.Task.Delay(Timeout.Infinite, spinnerCts.Token));

                            continue;
                        }
                        else if (msg.Contains("ToolsCalls:Completed") is true && !spinnerCts.IsCancellationRequested)
                        {
                            cancelSpinner();
                        }
                        else if (!string.IsNullOrWhiteSpace(msg))
                        {
                            spinStopThen(() => AnsiConsole.MarkupLineInterpolated($"[grey23]{msg}[/]"));
                        }

                        if (evt.Final is true)
                        {
                            spinStopThen(() => Console.WriteLine());
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

            try
            {
                var task = await agentCommSpinnerDef
                    .StartAsync("Communicating with Agent...", async ctx => await client.SendTaskAsync(request, agentCts.Token));

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
            finally
            {
                spinStopThen(() => Console.WriteLine());
            }
        }
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        spinStopThen(() => AnsiConsole.MarkupLineInterpolated($"[red]❌ Error: {ex.Message}[/]"));
    }
}

async System.Threading.Tasks.Task printRegistryAsync(CancellationToken cancellationToken)
{
    bool hasAgents = false;
    await foreach (var agent in client.GetRegistryAgentsAsync(cancellationToken))
    {
        if (!hasAgents)
        {
            hasAgents = true;
        }

        printCard(agent); ;
    }

    if (!hasAgents)
    {
        AnsiConsole.MarkupLine("[red]❌ No agents found.[/]");
        return;
    }
}

async System.Threading.Tasks.Task printAgentCardAsync()
{
    var agent = await client.GetAgentCardAsync();
    if (agent is null)
    {
        AnsiConsole.MarkupLine("[red]❌ No agents found.[/]");
        return;
    }
    else
    {
        printCard(agent);
    }
}

void printCard(AgentCard card)
{
    var detailsTable = new Table().AddColumn(string.Empty, c => c.RightAligned()).AddColumn(string.Empty).HideHeaders().NoBorder();
    if (card.Authentication is null)
    {
        detailsTable
            .AddRow($"[bold]{nameof(card.Authentication)}:[/]", "None");
    }
    else
    {
        var authDetailsTable = new Table().AddColumn("[underline]Schemes[/]", c => c.NoWrap().Centered()).AddColumn(new TableColumn(new Markup("[underline]Credentials[/]").Centered())).NoBorder().RoundedBorder()
            .AddRow(new Text(card.Authentication!.Schemes?.Count is null or 0 ? "None" : string.Join('\n', card.Authentication!.Schemes)),
                !string.IsNullOrWhiteSpace(card.Authentication.Credentials) ? new JsonText(card.Authentication.Credentials) : new Text("None"));

        detailsTable.AddRow(new Markup($"\n[bold]{nameof(card.Authentication)}:[/]"), authDetailsTable);
    }

    detailsTable
        .AddRow($"[bold]{nameof(card.Capabilities.Streaming)}:[/]", card.Capabilities.Streaming ? Emoji.Known.CheckMarkButton : Emoji.Known.CrossMarkButton)
        .AddRow($"[bold]{nameof(card.Capabilities.PushNotifications)}:[/]", card.Capabilities.PushNotifications ? Emoji.Known.CheckMarkButton : Emoji.Known.CrossMarkButton)
        .AddRow($"[bold]{nameof(card.Capabilities.StateTransitionHistory)}:[/]", card.Capabilities.StateTransitionHistory ? Emoji.Known.CheckMarkButton : Emoji.Known.CrossMarkButton)
        .AddRow(new Markup($"[bold]{nameof(card.Skills)}:[/]"), new Text(card.Skills.Count is 0 ? "None" : string.Join("\n", card.Skills.OrderBy(s => s.Name).Select(s => $"{Emoji.Known.Wrench} {s.Name}{(string.IsNullOrWhiteSpace(s.Description) ? string.Empty : $" - {s.Description}")}"))));

    var table = new Table().AddColumn(new(string.Empty) { Width = 50 }).AddColumn(string.Empty).HideHeaders().NoBorder();
    table.AddRow(new Markup($"[blue]{card.Provider?.Organization ?? string.Empty}\n{card.Provider?.Url}[/]"), new Markup($"[bold blue]{card.Name}[/]\n[blue]v{card.Version}[/]").RightJustified());
    table.AddRow(
        new Table().AddColumn(string.Empty).HideHeaders().NoBorder()
            .AddRow(new Markup($"[blue]{card.Description}[/]").RightJustified())
            .AddRow(new Text(card.DocumentationUrl?.ToString() ?? string.Empty).RightJustified()),
        detailsTable
    );

    table = new Table().AddColumn(string.Empty).HideHeaders().RoundedBorder()
        .AddRow(table);

    table = new Table().AddColumn(string.Empty).HideHeaders().HorizontalBorder()
        .AddRow(new Markup(card.Url.ToString(), new Style(Color.Green, decoration: Decoration.Underline | Decoration.Bold, link: card.Url.ToString())))
        .AddRow(table);

    AnsiConsole.Write(table);
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
                var bytes = Convert.FromBase64String(f.File.Bytes!);
                await System.IO.File.WriteAllBytesAsync(filename, bytes);
                AnsiConsole.MarkupLineInterpolated($"[darkgreen]Downloaded to: {filename}[/]");
                try
                {
                    var img = new CanvasImage(bytes);
                    img.MaxWidth(50);

                    AnsiConsole.MarkupLineInterpolated($"[darkgreen]Here's a rough rendering of it:[/]");
                    AnsiConsole.Write(img);
                }
                catch (Exception ex)
                {
                    // File might not be an image, so just bail on rendering it
                }
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