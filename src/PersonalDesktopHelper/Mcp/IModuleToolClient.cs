using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace PersonalDesktopHelper.Mcp;

public interface IModuleToolClient
{
    IReadOnlyList<Tool> Tools { get; }
    Task<CallToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken);
}
