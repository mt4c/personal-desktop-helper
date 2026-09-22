using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace PersonalDesktopHelper.Mcp;

internal static class ToolResults
{
    public static CallToolResult Success(object value) => new()
    {
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, JsonSerializerOptions.Web) }]
    };

    public static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };
}
