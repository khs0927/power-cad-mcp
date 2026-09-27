using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace PowerCad.Core.Transport;

/// <summary>Server-side connection to one plugin. One request in flight at a time.</summary>
public sealed class PipeClient(DiscoveryInfo target, TimeSpan? timeout = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(60);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private long _nextId;

    public DiscoveryInfo Target { get; } = target;

    public async Task<JsonNode?> SendAsync(string command, JsonObject? parameters, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            try
            {
                await EnsureConnectedAsync(cts.Token).ConfigureAwait(false);
                var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var request = Protocol.Request(id, Target.AuthToken, command, parameters);
                await _writer!.WriteLineAsync(request.ToJsonString(CadJson.Compact).AsMemory(), cts.Token).ConfigureAwait(false);
                var line = await _reader!.ReadLineAsync(cts.Token).ConfigureAwait(false)
                    ?? throw new IOException("The plugin closed the connection.");
                var response = JsonNode.Parse(line) as JsonObject ?? throw new IOException("Malformed response.");
                if (response["ok"]?.GetValue<bool>() == true)
                {
                    return response["result"];
                }

                var error = response["error"] as JsonObject;
                throw new CadException(
                    error?["code"]?.ToString() ?? ErrorCodes.Internal,
                    error?["message"]?.ToString() ?? "Unknown plugin error.",
                    error?["hint"]?.ToString());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Reset();
                throw new CadException(
                    ErrorCodes.Timeout,
                    $"AutoCAD did not answer '{command}' within {_timeout.TotalSeconds:0} s.",
                    "AutoCAD may be busy (running command, open dialog). Check it, press Esc if needed, then re-query before retrying an edit.");
            }
            catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
            {
                Reset();
                throw new CadException(ErrorCodes.NotConnected, $"Lost the connection to {Target.Target}: {e.Message}", "Call cad_status to reconnect.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_pipe is { IsConnected: true })
        {
            return;
        }

        Reset();
        _pipe = new NamedPipeClientStream(".", Target.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await _pipe.ConnectAsync(ct).ConfigureAwait(false);
        _reader = new StreamReader(_pipe, new UTF8Encoding(false), leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    private void Reset()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
        _pipe = null;
        _reader = null;
        _writer = null;
    }

    public ValueTask DisposeAsync()
    {
        Reset();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
