using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using PowerCad.Server;
using Xunit;

namespace PowerCad.Tests;

public sealed class HsSteelToolsTests
{
    private static string CanonicalJson(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject obj => "{" + string.Join(",", obj
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{JsonSerializer.Serialize(pair.Key)}:{CanonicalJson(pair.Value)}")) + "}",
        JsonArray arr => "[" + string.Join(",", arr.Select(CanonicalJson)) + "]",
        _ => node.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
    };

    private static string Digest(JsonNode node) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson(node))))
            .ToLowerInvariant();

    private static JsonObject Row(JsonObject spec, string mark) => new()
    {
        ["spec"] = spec,
        ["tag"] = new JsonObject
        {
            ["mark"] = mark,
            ["source"] = "hs-steel",
        },
    };

    private static JsonObject Handoff(int count = 2)
    {
        var entities = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            entities.Add(Row(
                new JsonObject
                {
                    ["type"] = "line",
                    ["layer"] = "STEEL",
                    ["start"] = new JsonArray(i * 10, 0),
                    ["end"] = new JsonArray(i * 10 + 5, 0),
                },
                $"B{i + 1}"));
        }

        var root = new JsonObject
        {
            ["schema"] = "hs-steel-draw-plan/1",
            ["producer"] = "khs0927/hs-steel-cad",
            ["title"] = "A-001",
            ["units"] = "mm",
            ["scale"] = 10,
            ["meta"] = new JsonObject { ["kind"] = "part" },
            ["entities"] = entities,
            ["execution_authorized"] = false,
            ["may_execute_mutation"] = false,
            ["requires_live_document_binding"] = true,
            ["tags_require_xdata_persistence"] = true,
        };
        root["contract_digest"] = Digest(root);
        return root;
    }

    private static JsonObject CatalogHandoff()
    {
        var root = new JsonObject
        {
            ["schema"] = "hs-steel-section-catalog/1",
            ["producer"] = "khs0927/hs-steel-cad",
            ["family"] = "H-BEAM",
            ["source_file"] = "H-BEAM.dat",
            ["source_sha256"] = new string('a', 64),
            ["encoding"] = "cp949",
            ["validation_status"] = "PASS",
            ["capability_scope"] = "single_family_file",
            ["global_legacy_catalog_verified"] = false,
            ["read_rows"] = 2,
            ["accepted_rows"] = 2,
            ["quarantined_rows"] = 0,
            ["query"] = null,
            ["returned_rows"] = 1,
            ["rows"] = new JsonArray
            {
                new JsonObject
                {
                    ["spec"] = "H100x100x6x8",
                    ["shape"] = "H",
                    ["dimensions_mm"] = new JsonArray(100, 100, 6, 8, 10, 0),
                    ["unit_weight_kg_m"] = 17.2,
                    ["paint_area_m2_m"] = 0.75,
                    ["aci_color"] = 3,
                    ["family"] = "H-BEAM",
                },
            },
            ["execution_authorized"] = false,
            ["may_execute_mutation"] = false,
        };
        root["contract_digest"] = Digest(root);
        return root;
    }

    private static JsonElement Element(JsonObject node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    [Fact]
    public void Valid_handoff_is_prepared_without_granting_execution()
    {
        var result = JsonNode.Parse(new HsSteelTools().Prepare(Element(Handoff())))!.AsObject();

        Assert.Equal("power-cad-hs-steel-prepared/1", result["schema"]!.GetValue<string>());
        Assert.True(result["source_digest_verified"]!.GetValue<bool>());
        Assert.Equal(2, result["entity_count"]!.GetValue<int>());
        Assert.Equal(1, result["chunk_count"]!.GetValue<int>());
        Assert.False(result["execution_authorized"]!.GetValue<bool>());
        Assert.False(result["may_execute_mutation"]!.GetValue<bool>());
        Assert.True(result["requires_snapshot_and_plan"]!.GetValue<bool>());
        Assert.False(result["xdata_write_supported"]!.GetValue<bool>());
        Assert.True(result["tag_persistence_required"]!.GetValue<bool>());

        var firstSpec = result["steps"]![0]!["params"]!["entities"]![0]!.AsObject();
        Assert.False(firstSpec.ContainsKey("tag"));
        Assert.False(firstSpec.ContainsKey("hs"));
        Assert.Equal(2, result["tag_manifest"]!.AsArray().Count);
    }

    [Fact]
    public void Digest_tampering_is_rejected()
    {
        var handoff = Handoff();
        handoff["entities"]![0]!["spec"]!["end"] = new JsonArray(999, 0);

        var error = Assert.Throws<McpException>(() => new HsSteelTools().Prepare(Element(handoff)));
        Assert.Contains("contract_digest", error.Message);
    }

    [Fact]
    public void Unknown_hs_field_inside_create_spec_is_rejected()
    {
        var handoff = Handoff();
        handoff["entities"]![0]!["spec"]!["hs"] = new JsonObject { ["mark"] = "BAD" };
        handoff["contract_digest"] = null;
        handoff.Remove("contract_digest");
        handoff["contract_digest"] = Digest(handoff);

        var error = Assert.Throws<McpException>(() => new HsSteelTools().Prepare(Element(handoff)));
        Assert.Contains("spec is invalid", error.Message);
    }

    [Fact]
    public void More_than_200_entities_are_chunked_into_atomic_create_steps()
    {
        var result = JsonNode.Parse(new HsSteelTools().Prepare(Element(Handoff(201))))!.AsObject();

        Assert.Equal(2, result["chunk_count"]!.GetValue<int>());
        Assert.Equal(200, result["steps"]![0]!["params"]!["entities"]!.AsArray().Count);
        Assert.Single(result["steps"]![1]!["params"]!["entities"]!.AsArray());
    }


    [Fact]
    public void Valid_section_catalog_handoff_stays_read_only_and_family_scoped()
    {
        var result = JsonNode.Parse(
            new HsSteelTools().PrepareCatalog(Element(CatalogHandoff())))!.AsObject();

        Assert.Equal("power-cad-hs-steel-catalog-prepared/1", result["schema"]!.GetValue<string>());
        Assert.True(result["source_digest_verified"]!.GetValue<bool>());
        Assert.Equal("H-BEAM", result["family"]!.GetValue<string>());
        Assert.Equal(1, result["row_count"]!.GetValue<int>());
        Assert.False(result["global_legacy_catalog_verified"]!.GetValue<bool>());
        Assert.False(result["execution_authorized"]!.GetValue<bool>());
        Assert.False(result["may_execute_mutation"]!.GetValue<bool>());
    }

    [Fact]
    public void Section_catalog_digest_tampering_is_rejected()
    {
        var handoff = CatalogHandoff();
        handoff["rows"]![0]!["unit_weight_kg_m"] = 99.0;

        var error = Assert.Throws<McpException>(
            () => new HsSteelTools().PrepareCatalog(Element(handoff)));
        Assert.Contains("contract_digest", error.Message);
    }

    [Fact]
    public void Section_catalog_rejects_invalid_physical_values_even_with_valid_digest()
    {
        var handoff = CatalogHandoff();
        handoff["rows"]![0]!["unit_weight_kg_m"] = -1.0;
        handoff.Remove("contract_digest");
        handoff["contract_digest"] = Digest(handoff);

        var error = Assert.Throws<McpException>(
            () => new HsSteelTools().PrepareCatalog(Element(handoff)));
        Assert.Contains("invalid physical", error.Message);
    }

    [Fact]
    public void Section_catalog_cannot_claim_global_legacy_verification()
    {
        var handoff = CatalogHandoff();
        handoff["global_legacy_catalog_verified"] = true;
        handoff.Remove("contract_digest");
        handoff["contract_digest"] = Digest(handoff);

        var error = Assert.Throws<McpException>(
            () => new HsSteelTools().PrepareCatalog(Element(handoff)));
        Assert.Contains("validation/safety", error.Message);
    }
}
