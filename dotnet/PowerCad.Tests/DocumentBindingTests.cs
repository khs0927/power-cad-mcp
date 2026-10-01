using System.Text.Json.Nodes;
using ModelContextProtocol;
using PowerCad.Core;
using PowerCad.Core.Commands;
using PowerCad.Core.Simulation;
using PowerCad.Server;
using Xunit;

namespace PowerCad.Tests;

public sealed class DocumentBindingTests
{
    private sealed class SwitchingGateway : ICadGateway
    {
        public SimulatorGateway Current { get; set; } = new(InMemoryCadDocument.CreateSample(), false);
        public string Mode => Current.Mode;
        public Task<JsonNode?> SendAsync(string command, JsonObject? p, CancellationToken ct) => Current.SendAsync(command, p, ct);
        public JsonArray ListTargets() => Current.ListTargets();
        public JsonObject SelectTarget(string target) => Current.SelectTarget(target);
    }

    private sealed class MemoryClient(string handle) : IOntologyContextClient
    {
        public bool Enabled => true;
        public Task<JsonObject> QueryGlobalMemoryAsync(string question, int topK, string? projectId, CancellationToken ct) =>
            Task.FromResult(new JsonObject { ["hits"] = new JsonArray(new JsonObject { ["geometry_ref"] = $"aec://geometry/{handle}" }) });
    }

    [Fact]
    public async Task Same_handle_and_fingerprint_in_another_drawing_cannot_receive_a_bound_edit()
    {
        var inner = new SwitchingGateway();
        await using var gateway = new DocumentBoundGateway(inner);
        var first = inner.Current.Document;
        var entities = (await gateway.SendAsync("query", new JsonObject(), default))!["entities"]!.AsArray();
        var handle = entities[0]!["handle"]!.GetValue<string>();
        var fingerprint = entities[0]!["fingerprint"]!.GetValue<string>();
        await gateway.SendAsync("bind_document", new JsonObject { ["document_id"] = first.DocumentId }, default);
        inner.Current = new SimulatorGateway(InMemoryCadDocument.CreateSample(), false);
        var other = new CommandDispatcher(inner.Current.Document).Execute("get", new JsonObject { ["handles"] = new JsonArray(handle) });
        Assert.Equal(fingerprint, other["entities"]![0]!["fingerprint"]!.GetValue<string>());
        var commitsBefore = inner.Current.Document.CommitCount;
        var error = await Assert.ThrowsAsync<CadException>(() => gateway.SendAsync("move",
            new JsonObject { ["targets"] = new JsonArray(new JsonObject { ["handle"] = handle, ["expect_fingerprint"] = fingerprint }),
                ["displacement"] = new JsonArray(10, 0) }, default));
        Assert.Equal(ErrorCodes.DocumentChanged, error.Code);
        Assert.Equal(commitsBefore, inner.Current.Document.CommitCount);
    }

    [Fact]
    public async Task Unbound_writes_fail_and_selecting_a_session_clears_binding()
    {
        var inner = new SwitchingGateway();
        await using var gateway = new DocumentBoundGateway(inner);
        var commitsBefore = inner.Current.Document.CommitCount;
        var create = new JsonObject { ["entities"] = new JsonArray(new JsonObject { ["type"] = "circle", ["center"] = new JsonArray(0, 0), ["radius"] = 10 }) };
        var error = await Assert.ThrowsAsync<CadException>(() => gateway.SendAsync("create", create, default));
        Assert.Equal(ErrorCodes.DocumentUnbound, error.Code);
        await gateway.SendAsync("bind_document", new JsonObject { ["document_id"] = inner.Current.Document.DocumentId }, default);
        await gateway.SendAsync("create", create, default);
        gateway.SelectTarget("simulator");
        error = await Assert.ThrowsAsync<CadException>(() => gateway.SendAsync("create", create, default));
        Assert.Equal(ErrorCodes.DocumentUnbound, error.Code);
        Assert.Equal(commitsBefore + 1, inner.Current.Document.CommitCount);
    }

    [Fact]
    public async Task Semantic_candidate_does_not_resolve_after_drawing_switch_even_when_entities_match()
    {
        var gateway = new SwitchingGateway();
        var read = await gateway.SendAsync("query", new JsonObject(), default);
        var handle = read!["entities"]![0]!["handle"]!.GetValue<string>();
        var context = new OntologyContextTools(gateway, new MemoryClient(handle), new OntologyCandidateStore());
        var result = JsonNode.Parse(await context.Query("entity"))!;
        gateway.Current = new SimulatorGateway(InMemoryCadDocument.CreateSample(), false);
        var error = await Assert.ThrowsAsync<CadException>(() => context.Select(result["context_id"]!.GetValue<string>(), 1));
        Assert.Equal(ErrorCodes.DocumentChanged, error.Code);
    }

    [Fact]
    public void Identity_is_stable_across_rename_but_changes_on_reopen()
    {
        var doc = InMemoryCadDocument.CreateSample();
        var id = doc.DocumentId;
        doc.Name = "Renamed.dwg";
        Assert.Equal(id, doc.Describe()["document_id"]!.GetValue<string>());
        Assert.NotEqual(id, InMemoryCadDocument.CreateSample().DocumentId);
        var dispatcher = new CommandDispatcher(doc);
        var error = Assert.Throws<CadException>(() => dispatcher.Execute("get",
            new JsonObject { ["expected_document_id"] = "another-drawing", ["handles"] = new JsonArray("1") }));
        Assert.Equal(ErrorCodes.DocumentChanged, error.Code);
    }
}
