using System.ComponentModel;
using ModelContextProtocol.Server;
using DecompilerServer.Services;

namespace DecompilerServer;

[McpServerToolType]
public static class PingTool
{
    [McpServerTool, Description("Connectivity check. Returns 'pong' and current MVID if loaded.")]
    public static string Ping()
    {
        return ResponseFormatter.TryExecute(() =>
        {
            var timeUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var currentSession = ServiceLocator.Workspace != null
                ? ToolSessionRouter.TryGetCurrentLoaded()
                : null;
            var mvid = ServiceLocator.Workspace != null
                ? currentSession?.ContextManager.Mvid
                : ServiceLocator.ContextManager.IsLoaded ? ServiceLocator.ContextManager.Mvid : null;

            var result = new
            {
                pong = true,
                mvid,
                timeUnix = timeUnix
            };

            return result;
        });
    }
}
