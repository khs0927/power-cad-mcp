using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerCad.Core.Transport;

/// <summary>
/// Named-pipe NDJSON listener used inside the CAD host. It never touches the CAD API itself: every
/// request is handed to <paramref name="handler"/>, which is responsible for marshalling onto the CAD
/// main thread. Requests are authenticated with the token from the discovery file.
/// </summary>
public sealed class PipeServer(string pipeName, string token, Func<string, JsonObject?, CancellationToken, Task<JsonNode>> handler, int maxConnections = 4)
    : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];
    private readonly byte[] _token = Encoding.UTF8.GetBytes(token);

    public string PipeName { get; } = pipeName;

    public void Start()
    {
        for (var i = 0; i < maxConnections; i++)
        {
            _loops.Add(Task.Run(() => AcceptLoop(_cts.Token)));
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    maxConnections,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await ServeAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // client went away; accept the next one
            }
            finally
            {
                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ServeAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            var response = await HandleLineAsync(line, ct).ConfigureAwait(false);
            await writer.WriteLineAsync(response.ToJsonString(CadJson.Compact)).ConfigureAwait(false);
        }
    }

    public async Task<JsonObject> HandleLineAsync(string line, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(line) > Protocol.MaxMessageBytes)
        {
            return Protocol.Failure(null, ErrorCodes.InvalidParams, "Request exceeds 1 MiB.");
        }

        JsonObject? request;
        try
        {
            request = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null)
        {
            return Protocol.Failure(null, ErrorCodes.InvalidParams, "Request is not a JSON object.");
        }

        var id = request["id"]?.ToString();
        var presented = Encoding.UTF8.GetBytes(request["token"]?.ToString() ?? "");
        if (!CryptographicOperations.FixedTimeEquals(presented, _token))
        {
            return Protocol.Failure(id, ErrorCodes.Unauthorized, "Invalid token.", "Re-read the discovery file; the plugin may have restarted.");
        }

        var command = request["command"]?.ToString();
        if (string.IsNullOrEmpty(command))
        {
            return Protocol.Failure(id, ErrorCodes.InvalidParams, "'command' is required.");
        }

        try
        {
            var result = await handler(command, request["params"] as JsonObject, ct).ConfigureAwait(false);
            var response = Protocol.Success(id, result);
            if (Encoding.UTF8.GetByteCount(response.ToJsonString(CadJson.Compact)) > Protocol.MaxMessageBytes)
            {
                return Protocol.Failure(id, ErrorCodes.TooManyMatches, "Response exceeds 1 MiB.", "Narrow the query or lower max_results.");
            }

            return response;
        }
        catch (CadException e)
        {
            return Protocol.Failure(id, e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Protocol.Failure(id, ErrorCodes.Internal, $"{e.GetType().Name}: {Sanitize(e.Message)}");
        }
    }

    /// <summary>Strip local file-system paths from unexpected error messages before they leave the host.</summary>
    public static string Sanitize(string message) =>
        System.Text.RegularExpressions.Regex.Replace(message, @"([A-Za-z]:\\|/home/|/Users/)[^\s'""]*", "<path>");

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
        }

        _cts.Dispose();
    }
}
