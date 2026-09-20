using FileInteractionMCP.Sandbox;
using FileInteractionMCP.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Our own flags (--root, --trash, ...) are parsed here; the host builder never sees them.
var sandbox = SandboxOptions.FromArgs(args);

var builder = Host.CreateApplicationBuilder();

// stdout carries the MCP protocol, so every log line goes to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(sandbox);
builder.Services.AddSingleton<PathGuard>();
builder.Services.AddSingleton<UndoJournal>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<FileSystemTools>()
    .WithTools<EditTools>()
    .WithTools<ReadTools>()
    .WithTools<HistoryTools>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation(
    "FileInteractionMCP roots: {Roots}; trash: {Trash}",
    string.Join("; ", sandbox.Roots), sandbox.TrashDirectory);

var pruned = host.Services.GetRequiredService<UndoJournal>().PruneOrphans();
if (pruned > 0)
{
    logger.LogInformation("Removed {Count} orphaned undo snapshot folder(s).", pruned);
}

await host.RunAsync();
