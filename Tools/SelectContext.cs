using System.ComponentModel;
using ModelContextProtocol.Server;
using DecompilerServer.Services;

namespace DecompilerServer;

[McpServerToolType]
public static class SelectContextTool
{
    [McpServerTool, Description("Select the current assembly context by loaded or registered alias. Registered contexts are loaded on demand.")]
    public static string SelectContext(string contextAlias)
    {
        return ResponseFormatter.TryExecute(() =>
        {
            var workspace = ServiceLocator.Workspace
                ?? throw new InvalidOperationException("Workspace support is not registered.");

            return workspace.SelectContext(contextAlias);
        });
    }
}
