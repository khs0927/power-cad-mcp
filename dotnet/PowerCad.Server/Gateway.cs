using System.Text.Json.Nodes;
using PowerCad.Core;
using PowerCad.Core.Commands;
using PowerCad.Core.Simulation;
using PowerCad.Core.Transport;

namespace PowerCad.Server;

/// <summary>Where tool calls go: a running AutoCAD plugin (named pipe) or the in-process simulator.</summary>
public interface ICadGateway
{
    Task<JsonNode?> SendAsync(string command, JsonObject? parameters, CancellationToken ct);

    JsonArray ListTargets();

    JsonObject SelectTarget(string target);

    string Mode { get; }
}

public sealed class SimulatorGateway(InMemoryCadDocument document, bool readOnly) : ICadGateway
{
    private readonly CommandDispatcher _dispatcher = new(document, new DispatcherOptions { ReadOnly = readOnly });

    public string Mode => "simulator";

    public InMemoryCadDocument Document { get; } = document;

    public Task<JsonNode?> SendAsync(string command, JsonObject? parameters, CancellationToken ct) =>
        Task.FromResult<JsonNode?>(_dispatcher.Execute(command, parameters?.DeepClone() as JsonObject));

    public JsonArray ListTargets() => [new JsonObject { ["target"] = "simulator", ["product"] = "simulator", ["selected"] = true }];

    public JsonObject SelectTarget(string target) => target == "simulator"
        ? new JsonObject { ["selected"] = "simulator" }
        : throw new CadException(ErrorCodes.NotFound, "Only the 'simulator' target exists in --simulate mode.");
}

/// <summary>Connects to AutoCAD plugins found through discovery files; reconnects after restarts.</summary>
public sealed class PipeGateway(DiscoveryStore store, string? pinnedTarget, bool readOnly) : ICadGateway, IAsyncDisposable
{
    private readonly Lock _lock = new();
    private PipeClient? _client;
    private string? _pinned = pinnedTarget;

    public string Mode => "autocad";

    public async Task<JsonNode?> SendAsync(string command, JsonObject? parameters, CancellationToken ct)
    {
        if (readOnly && CommandDispatcher.Mutating.Contains(command))
        {
            throw new CadException(ErrorCodes.ReadOnly, "The server runs with --read-only.", "Restart without --read-only to allow edits.");
        }

        var client = Resolve();
        try
        {
            return await client.SendAsync(command, parameters, ct).ConfigureAwait(false);
        }
        catch (CadException e) when (e.Code is ErrorCodes.NotConnected or ErrorCodes.Unauthorized)
        {
            Drop(client);
            if (command is "status" or "query" or "get" or "inspect" or "layers")
            {
                // Read-only calls are safe to retry once against a freshly discovered plugin.
                return await Resolve().SendAsync(command, parameters, ct).ConfigureAwait(false);
            }

            throw;
        }
    }

    private PipeClient Resolve()
    {
        lock (_lock)
        {
            var live = store.List();
            if (_client is not null && live.Any(i => i.Pid == _client.Target.Pid && i.AuthToken == _client.Target.AuthToken))
            {
                return _client;
            }

            _ = _client?.DisposeAsync();
            _client = null;
            if (live.Count == 0)
            {
                throw new CadException(
                    ErrorCodes.NotConnected,
                    "No running AutoCAD with the Power CAD plugin was found.",
                    "Start AutoCAD 2027 with the PowerCad bundle installed (see README), open a drawing, then call cad_status again.");
            }

            var target = _pinned is null
                ? live[0]
                : live.FirstOrDefault(i => string.Equals(i.Target, _pinned, StringComparison.OrdinalIgnoreCase) || i.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture) == _pinned)
                    ?? throw new CadException(ErrorCodes.NotFound, $"Target '{_pinned}' is not running.", "Call cad_list_targets.");
            _client = new PipeClient(target);
            return _client;
        }
    }

    private void Drop(PipeClient client)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_client, client))
            {
                _ = _client.DisposeAsync();
                _client = null;
            }
        }
    }

    public JsonArray ListTargets()
    {
        var current = _client?.Target.Target;
        return new JsonArray(store.List().Select(i =>
        {
            var o = DiscoveryStore.Describe(i);
            o["selected"] = i.Target == current;
            return (JsonNode)o;
        }).ToArray());
    }

    public JsonObject SelectTarget(string target)
    {
        lock (_lock)
        {
            _pinned = target;
            _ = _client?.DisposeAsync();
            _client = null;
        }

        var client = Resolve();
        return new JsonObject { ["selected"] = client.Target.Target };
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
