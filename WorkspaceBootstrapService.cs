using DecompilerServer.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DecompilerServer;

public sealed class WorkspaceBootstrapService(
    ILogger<WorkspaceBootstrapService> log,
    DecompilerWorkspace workspace) : IHostedService
{
    private readonly ILogger<WorkspaceBootstrapService> _log = log;
    private readonly DecompilerWorkspace _workspace = workspace;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var registeredAliases = _workspace.ListRegisteredAliases();

        if (registeredAliases.Count == 0)
        {
            _log.LogInformation("No registered workspace aliases found.");
            return Task.CompletedTask;
        }

        _log.LogInformation(
            "Registered {Count} workspace aliases for on-demand loading. Current alias: {CurrentAlias}",
            registeredAliases.Count,
            _workspace.CurrentContextAlias);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
