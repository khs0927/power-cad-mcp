using System.Text.Json.Nodes;
using PowerCad.Core;
using PowerCad.Core.Commands;

namespace PowerCad.Server;

/// <summary>One client binding; the plugin checks it again inside the CAD document lock.</summary>
public sealed class DocumentBoundGateway(ICadGateway inner) : ICadGateway, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _documentId;
    public string Mode => inner.Mode;

    public async Task<JsonNode?> SendAsync(string command, JsonObject? parameters, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var p = parameters?.DeepClone() as JsonObject ?? new JsonObject();
            if (command == "bind_document")
            {
                var id = p["document_id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id)) throw CadException.Invalid("document_id is required.");
                var identity = await inner.SendAsync("document_identity", new JsonObject { ["expected_document_id"] = id }, ct).ConfigureAwait(false);
                if (identity?["document_id"]?.GetValue<string>() != id)
                    throw new CadException(ErrorCodes.PluginOutdated, "The plugin does not support document binding.");
                _documentId = id;
                return identity;
            }

            // Discovery remains available after the user switches drawings, so they can explicitly rebind.
            if (command is "status" or "document_identity")
                return await inner.SendAsync(command, p, ct).ConfigureAwait(false);

            var explicitId = p["expected_document_id"]?.GetValue<string>();
            if (_documentId is not null)
            {
                if (explicitId is not null && explicitId != _documentId)
                    throw new CadException(ErrorCodes.DocumentChanged, "The request and client are bound to different drawings.");
                p["expected_document_id"] = _documentId;
            }

            if ((CommandDispatcher.Mutating.Contains(command) || command is "save" or "export_block" or "export_hatch_pattern")
                && _documentId is null)
                throw new CadException(ErrorCodes.DocumentUnbound,
                    "Bind the intended drawing before editing or exporting it.",
                    "Call cad_get_document_identity, then cad_bind_document with its document_id.");

            return await inner.SendAsync(command, p, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public JsonArray ListTargets() => inner.ListTargets();

    public JsonObject SelectTarget(string target)
    {
        _gate.Wait();
        try
        {
            var result = inner.SelectTarget(target);
            _documentId = null;
            return result;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (inner is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
