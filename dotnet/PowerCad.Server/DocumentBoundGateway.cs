using System.Text.Json.Nodes;
using PowerCad.Core;
using PowerCad.Core.Commands;

namespace PowerCad.Server;

/// <summary>One client binding; the plugin checks it again inside the CAD document lock.</summary>
public sealed class DocumentBoundGateway(ICadGateway inner) : ICadGateway, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _documentId;
    private JsonObject? _sourceBinding;
    private JsonObject? _boundIdentity;
    public string Mode => inner.Mode;

    public string? BoundDocumentId => _documentId;
    public JsonObject? BoundDocument => _boundIdentity?.DeepClone().AsObject();
    public JsonObject? SourceBindingSnapshot() => _sourceBinding?.DeepClone().AsObject();

    private static void ValidateSourceBinding(JsonObject binding, string documentId)
    {
        string Required(string name) =>
            binding[name] is JsonValue node && node.TryGetValue<string>(out var value)
                && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw CadException.Invalid($"source_binding.{name} is required.");

        if (Required("schema") != "aec-executor-handoff/1")
            throw CadException.Invalid("source_binding.schema must be aec-executor-handoff/1.");
        if (Required("binding_state") != "SOURCE_BOUND")
            throw CadException.Invalid("source_binding must represent SOURCE_BOUND.");
        if (Required("review_status") != "VERIFIED_FOR_REVIEW")
            throw CadException.Invalid("source_binding must be VERIFIED_FOR_REVIEW.");
        if (Required("document_id") != documentId)
            throw new CadException(ErrorCodes.DocumentChanged, "Ontology source binding belongs to a different live document.");
        foreach (var name in new[]
                 {
                     "source_id", "source_byte_revision_id", "parser_revision_id",
                     "handoff_digest", "resolver_receipt_sha256"
                 })
            _ = Required(name);

        foreach (var name in new[] { "handoff_digest", "resolver_receipt_sha256" })
        {
            var digest = Required(name);
            if (digest.Length != 64 || digest.Any(ch => !Uri.IsHexDigit(ch)))
                throw CadException.Invalid($"source_binding.{name} must be a SHA-256 hex digest.");
        }

        bool Flag(string name, bool expected) =>
            binding[name] is JsonValue node && node.TryGetValue<bool>(out var value) && value == expected;

        if (!Flag("execution_authorized", false)
            || !Flag("may_execute_mutation", false)
            || !Flag("requires_executor_authorization", true))
            throw CadException.Invalid("Ontology source binding must remain non-authorizing.");
    }

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
                JsonObject? sourceBinding = null;
                if (p["source_binding"] is JsonObject binding)
                {
                    ValidateSourceBinding(binding, id);
                    sourceBinding = binding.DeepClone().AsObject();
                }
                else if (p.ContainsKey("source_binding"))
                {
                    throw CadException.Invalid("source_binding must be an object.");
                }

                var identity = await inner.SendAsync("document_identity", new JsonObject { ["expected_document_id"] = id }, ct).ConfigureAwait(false);
                if (identity?["document_id"]?.GetValue<string>() != id)
                    throw new CadException(ErrorCodes.PluginOutdated, "The plugin does not support document binding.");
                _documentId = id;
                _boundIdentity = identity?.DeepClone().AsObject();
                _sourceBinding = sourceBinding;
                if (_sourceBinding is not null && identity is JsonObject identityObject)
                    identityObject["source_binding"] = _sourceBinding.DeepClone();
                return identity;
            }

            // Discovery remains available after the user switches drawings, so they can explicitly rebind.
            if (command is "status" or "document_identity")
            {
                var discovery = await inner.SendAsync(command, p, ct).ConfigureAwait(false);
                if (command == "document_identity"
                    && _documentId is not null
                    && _sourceBinding is not null
                    && discovery is JsonObject identity
                    && identity["document_id"]?.GetValue<string>() == _documentId)
                    identity["source_binding"] = _sourceBinding.DeepClone();
                return discovery;
            }

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
            _boundIdentity = null;
            _sourceBinding = null;
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
