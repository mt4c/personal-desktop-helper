using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace PersonalDesktopHelper.Mcp;

public sealed class LocalMcpConnection : IModuleToolClient, IAsyncDisposable
{
    private readonly IReadOnlyList<McpServerTool> _serverTools;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private McpServer? _server;
    private McpClient? _client;
    private Task? _serverRun;
    private bool _disposed;

    public LocalMcpConnection(DesktopModuleTools moduleTools)
    {
        _serverTools = moduleTools.CreateTools();
        Tools = _serverTools.Select(tool => tool.ProtocolTool).ToArray();
    }

    public IReadOnlyList<Tool> Tools { get; }

    public async Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return tools.Select(tool => tool.ProtocolTool).ToArray();
    }

    public async Task<CallToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tool = Tools.FirstOrDefault(tool => tool.Name == name);
        if (tool is null)
        {
            return ToolResults.Error($"Unknown module tool: {name}.");
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return ToolResults.Error("Tool arguments must be a JSON object.");
        }

        var properties = tool.InputSchema.GetProperty("properties");
        if (arguments.EnumerateObject().Any(argument => !properties.TryGetProperty(argument.Name, out _)))
        {
            return ToolResults.Error("Tool arguments contain an unknown parameter.");
        }

        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.GetRawText())!;
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        var requestId = new RequestId(Guid.NewGuid().ToString());
        try
        {
            return await client.SendRequestAsync<CallToolRequestParams, CallToolResult>(
                RequestMethods.ToolsCall, new CallToolRequestParams { Name = name, Arguments = values },
                requestId: requestId, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation can race with the SDK registering its notification after sending.
            await client.SendNotificationAsync(NotificationMethods.CancelledNotification,
                new CancelledNotificationParams { RequestId = requestId },
                cancellationToken: _shutdown.Token).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<McpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client is not null)
            {
                return _client;
            }

            var requests = new Pipe();
            var responses = new Pipe();
            var options = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "PersonalDesktopHelper.Modules", Version = "1.0.0" },
                ToolCollection = new()
            };
            foreach (var tool in _serverTools)
            {
                options.ToolCollection.Add(tool);
            }

            _server = McpServer.Create(new StreamServerTransport(
                requests.Reader.AsStream(), responses.Writer.AsStream()), options);
            _serverRun = _server.RunAsync(_shutdown.Token);
            try
            {
                _client = await McpClient.CreateAsync(new StreamClientTransport(
                    requests.Writer.AsStream(), responses.Reader.AsStream()),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                return _client;
            }
            catch
            {
                await _server.DisposeAsync().ConfigureAwait(false);
                _server = null;
                var serverRun = _serverRun;
                _serverRun = null;
                if (serverRun is not null)
                {
                    await serverRun.ConfigureAwait(false);
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _shutdown.CancelAsync().ConfigureAwait(false);
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
            }

            if (_server is not null)
            {
                await _server.DisposeAsync().ConfigureAwait(false);
            }

            if (_serverRun is not null)
            {
                try
                {
                    await _serverRun.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    // The server's run loop ends when the app quits.
                }
            }

            _shutdown.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}
