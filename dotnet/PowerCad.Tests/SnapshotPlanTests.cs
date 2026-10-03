using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using PowerCad.Core;
using PowerCad.Core.Commands;
using PowerCad.Core.Simulation;
using PowerCad.Server;
using Xunit;

namespace PowerCad.Tests;

public sealed class SnapshotPlanTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("powercad-plan-").FullName;
    public void Dispose() => Directory.Delete(_root, true);
    private static JsonObject Obj(string value) => JsonNode.Parse(value)!.AsObject();
    private static JsonElement Steps(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    private static JsonObject SourceBinding(string documentId) => new()
    {
        ["schema"] = "aec-executor-handoff/1",
        ["binding_state"] = "SOURCE_BOUND",
        ["review_status"] = "VERIFIED_FOR_REVIEW",
        ["document_id"] = documentId,
        ["source_id"] = new string('a', 64),
        ["source_byte_revision_id"] = new string('b', 64),
        ["parser_revision_id"] = new string('c', 64),
        ["handoff_digest"] = new string('d', 64),
        ["resolver_receipt_sha256"] = new string('e', 64),
        ["execution_authorized"] = false,
        ["may_execute_mutation"] = false,
        ["requires_executor_authorization"] = true,
    };

    [Fact]
    public async Task Snapshot_reports_truncation_and_pages_remain_frozen_after_live_edits()
    {
        var doc = InMemoryCadDocument.CreateSample();
        var gateway = new SimulatorGateway(doc, false);
        var store = new SnapshotStore();
        var tools = new SnapshotTools(gateway, store);
        var metadata = Obj(await tools.Extract(max_entities: 1));
        Assert.True(metadata["truncated"]!.GetValue<bool>());
        Assert.True(metadata["raw_count"]!.GetValue<int>() > 1);
        Assert.Equal(metadata["raw_count"]!.GetValue<int>(), metadata["processed_count"]!.GetValue<int>());
        Assert.Contains("paper_space", metadata["excluded_scopes"]!.AsArray().Select(n => n!.GetValue<string>()));
        var id = metadata["snapshot_id"]!.GetValue<string>();
        var before = tools.Page(id);
        var first = Obj(before)["entities"]![0]!;
        await gateway.SendAsync("move", new JsonObject { ["handles"] = new JsonArray(first["handle"]!.GetValue<string>()), ["displacement"] = new JsonArray(10, 0) }, default);
        Assert.Equal(before, tools.Page(id));
        Assert.NotEqual(metadata["content_hash"]!.GetValue<string>(), Obj(await tools.Extract(1))["content_hash"]!.GetValue<string>());
        Assert.False(Obj(before)["live_currentness_verified"]!.GetValue<bool>());
    }

    private async Task<(InMemoryCadDocument Doc, DocumentBoundGateway Gateway, SnapshotStore Snapshots, string Snapshot)> Setup()
    {
        var doc = InMemoryCadDocument.CreateSample();
        var gateway = new DocumentBoundGateway(new SimulatorGateway(doc, false));
        await gateway.SendAsync("bind_document", new JsonObject { ["document_id"] = doc.DocumentId }, default);
        var snapshots = new SnapshotStore();
        var snapshot = Obj(await new SnapshotTools(gateway, snapshots).Extract())["snapshot_id"]!.GetValue<string>();
        return (doc, gateway, snapshots, snapshot);
    }

    [Fact]
    public async Task Review_uses_only_confirmed_layer_colors_and_reports_duplicates_without_mutating()
    {
        var doc = new InMemoryCadDocument();
        var dispatcher = new CommandDispatcher(doc);
        dispatcher.Execute("set_layer", new JsonObject { ["name"] = "WAL1", ["color"] = 2 });
        dispatcher.Execute("set_layer", new JsonObject { ["name"] = "WIN", ["color"] = 1 });
        dispatcher.Execute("create", JsonNode.Parse("""{"entities":[{"type":"line","start":[0,0],"end":[1,0],"layer":"WAL1"},{"type":"line","start":[0,0],"end":[1,0],"layer":"WAL1"}]}""")!.AsObject());
        var snapshots = new SnapshotStore();
        var metadata = Obj(await new SnapshotTools(new SimulatorGateway(doc, false), snapshots).Extract());
        var count = doc.CommitCount;
        var review = Obj(new ReviewTools(snapshots).Review(metadata["snapshot_id"]!.GetValue<string>()));
        var findings = review["findings"]!.AsArray();
        Assert.Contains(findings, n => n!["code"]!.GetValue<string>() == "EXACT_DUPLICATE_STATE");
        Assert.Contains(findings, n => n!["code"]!.GetValue<string>() == "LAYER_COLOR_MISMATCH" && n["layer"]!.GetValue<string>() == "WAL1");
        Assert.DoesNotContain(findings, n => n!["code"]!.GetValue<string>() == "LAYER_COLOR_MISMATCH" && n["layer"]!.GetValue<string>() == "WIN");
        Assert.False(review["may_execute_mutation"]!.GetValue<bool>());
        Assert.Equal(count, doc.CommitCount);
    }

    [Fact]
    public async Task Plan_requires_preview_and_repeated_application_returns_persisted_receipt_without_duplication()
    {
        var (doc, gateway, snapshots, snapshot) = await Setup();
        await using (gateway)
        {
            var store = new PlanStore(_root);
            var tools = new PlanTools(gateway, snapshots, store);
            var plan = Obj(await tools.Create(snapshot, Steps("""[{"command":"create","params":{"entities":[{"type":"circle","center":[0,0],"radius":7}]}}]"""), "test circle"));
            var id = plan["plan_id"]!.GetValue<string>();
            var count = doc.CommitCount;
            await Assert.ThrowsAsync<McpException>(() => tools.Execute(id, false));
            Assert.Equal("Previewed", Obj(await tools.Execute(id))["state"]!.GetValue<string>());
            Assert.Equal(count, doc.CommitCount);
            Assert.Equal("Committed", Obj(await tools.Execute(id, false))["state"]!.GetValue<string>());
            var reopened = new PlanTools(gateway, snapshots, new PlanStore(_root));
            Assert.Equal("Committed", Obj(await reopened.Execute(id, false))["state"]!.GetValue<string>());
            Assert.Equal(count + 1, doc.CommitCount);
        }
    }

    [Fact]
    public async Task Source_bound_ontology_handoff_is_persisted_into_plan_and_receipt()
    {
        var doc = InMemoryCadDocument.CreateSample();
        var gateway = new DocumentBoundGateway(new SimulatorGateway(doc, false));
        var binding = SourceBinding(doc.DocumentId);
        var bindResult = (await gateway.SendAsync(
            "bind_document",
            new JsonObject
            {
                ["document_id"] = doc.DocumentId,
                ["source_binding"] = binding.DeepClone(),
            },
            default))!.AsObject();
        Assert.Equal("SOURCE_BOUND", bindResult["source_binding"]!["binding_state"]!.GetValue<string>());

        await using (gateway)
        {
            var snapshots = new SnapshotStore();
            var snapshot = Obj(await new SnapshotTools(gateway, snapshots).Extract())["snapshot_id"]!.GetValue<string>();
            var tools = new PlanTools(gateway, snapshots, new PlanStore(_root));
            var plan = Obj(await tools.Create(
                snapshot,
                Steps("""[{"command":"create","params":{"entities":[{"type":"circle","center":[0,0],"radius":7}]}}]"""),
                "source bound test"));
            Assert.Equal(new string('d', 64), plan["source_binding"]!["handoff_digest"]!.GetValue<string>());

            var id = plan["plan_id"]!.GetValue<string>();
            await tools.Execute(id);
            var committed = Obj(await tools.Execute(id, false));
            Assert.Equal("Committed", committed["state"]!.GetValue<string>());
            Assert.Equal("power-cad", committed["result"]!["executor"]!.GetValue<string>());
            Assert.Equal(id, committed["result"]!["plan_id"]!.GetValue<string>());
            Assert.Equal(doc.DocumentId, committed["result"]!["document_id"]!.GetValue<string>());
            Assert.Equal(new string('d', 64), committed["result"]!["source_binding_handoff_digest"]!.GetValue<string>());
            Assert.Equal(new string('a', 64), committed["result"]!["source_id"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Source_binding_for_another_document_is_rejected()
    {
        var doc = InMemoryCadDocument.CreateSample();
        await using var gateway = new DocumentBoundGateway(new SimulatorGateway(doc, false));
        var error = await Assert.ThrowsAsync<CadException>(() => gateway.SendAsync(
            "bind_document",
            new JsonObject
            {
                ["document_id"] = doc.DocumentId,
                ["source_binding"] = SourceBinding("other-document"),
            },
            default));
        Assert.Equal(ErrorCodes.DocumentChanged, error.Code);
    }

    [Theory]
    [InlineData("resolver_receipt_sha256", "\"invalid\"")]
    [InlineData("resolver_receipt_sha256", "\"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeg\"")]
    [InlineData("handoff_digest", "123")]
    [InlineData("source_id", "\"   \"")]
    [InlineData("parser_revision_id", "{}")]
    [InlineData("source_byte_revision_id", "null")]
    [InlineData("execution_authorized", "\"false\"")]
    [InlineData("may_execute_mutation", "true")]
    [InlineData("requires_executor_authorization", "false")]
    public async Task Malformed_source_binding_is_rejected_without_replacing_existing_binding(string field, string json)
    {
        var doc = InMemoryCadDocument.CreateSample();
        await using var gateway = new DocumentBoundGateway(new SimulatorGateway(doc, false));
        var valid = SourceBinding(doc.DocumentId);
        await gateway.SendAsync("bind_document", new JsonObject
        {
            ["document_id"] = doc.DocumentId,
            ["source_binding"] = valid.DeepClone(),
        }, default);
        var invalid = valid.DeepClone().AsObject();
        invalid[field] = JsonNode.Parse(json);
        var count = doc.CommitCount;
        var error = await Assert.ThrowsAsync<CadException>(() => gateway.SendAsync("bind_document", new JsonObject
        {
            ["document_id"] = doc.DocumentId,
            ["source_binding"] = invalid,
        }, default));
        Assert.Equal(ErrorCodes.InvalidParams, error.Code);
        Assert.True(JsonNode.DeepEquals(valid, gateway.SourceBindingSnapshot()));
        Assert.Equal(count, doc.CommitCount);
    }

    [Fact]
    public async Task Stale_plan_target_rejects_batch_and_records_failure()
    {
        var (doc, gateway, snapshots, snapshot) = await Setup();
        await using (gateway)
        {
            var first = snapshots.Get(snapshot)["entities"]![0]!;
            var handle = first["handle"]!.GetValue<string>();
            var tools = new PlanTools(gateway, snapshots, new PlanStore(_root));
            var plan = Obj(await tools.Create(snapshot, Steps("""[{"command":"move","params":{"targets":[{"handle":"HANDLE"}],"displacement":[10,0]}}]""".Replace("HANDLE", handle)), "move captured entity"));
            Assert.Equal(first["fingerprint"]!.GetValue<string>(), plan["steps"]![0]!["params"]!["targets"]![0]!["expect_fingerprint"]!.GetValue<string>());
            var id = plan["plan_id"]!.GetValue<string>();
            await tools.Execute(id);
            await gateway.SendAsync("move", new JsonObject { ["handles"] = new JsonArray(handle), ["displacement"] = new JsonArray(1, 0) }, default);
            var count = doc.CommitCount;
            var error = await Assert.ThrowsAsync<McpException>(() => tools.Execute(id, false));
            Assert.StartsWith("[STALE_TARGET]", error.Message);
            Assert.Equal(count, doc.CommitCount);
            Assert.Equal("Failed", Obj(tools.Get(id))["state"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Interrupted_and_cross_process_locked_plans_cannot_be_replayed()
    {
        var (_, gateway, snapshots, snapshot) = await Setup();
        await using (gateway)
        {
            var store = new PlanStore(_root);
            var tools = new PlanTools(gateway, snapshots, store);
            var plan = Obj(await tools.Create(snapshot, Steps("""[{"command":"create","params":{"entities":[{"type":"circle","center":[0,0],"radius":7}]}}]"""), "test"));
            var id = plan["plan_id"]!.GetValue<string>();
            using (store.AcquireExecution(id))
            {
                var other = new PlanTools(gateway, snapshots, new PlanStore(_root));
                var busy = await Assert.ThrowsAsync<McpException>(() => other.Execute(id));
                Assert.StartsWith("[PLAN_BUSY]", busy.Message);
            }
            plan["state"] = "Executing";
            store.Write(plan);
            var error = await Assert.ThrowsAsync<McpException>(() => tools.Execute(id, false));
            Assert.StartsWith("[PLAN_STATE]", error.Message);
        }
    }
}
