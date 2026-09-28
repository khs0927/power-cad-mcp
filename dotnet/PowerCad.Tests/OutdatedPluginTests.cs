using System.Text.Json.Nodes;
using ModelContextProtocol;
using PowerCad.Core;
using PowerCad.Server;
using Xunit;

namespace PowerCad.Tests;

public sealed class OutdatedPluginTests
{
    /// <summary>Imitates a 0.2 plugin that only knows the original eight commands.</summary>
    private sealed class OldPluginGateway : ICadGateway
    {
        public string Mode => "autocad";

        public Task<JsonNode?> SendAsync(string command, JsonObject? parameters, CancellationToken ct) =>
            command == "status"
                ? Task.FromResult<JsonNode?>(new JsonObject { ["plugin_version"] = "0.2.0" })
                : throw new CadException(ErrorCodes.UnknownCommand, $"Unknown command '{command}'.");

        public JsonArray ListTargets() => [];

        public JsonObject SelectTarget(string target) => [];
    }

    [Fact]
    public async Task New_tools_against_an_old_plugin_say_how_to_update()
    {
        var tools = new CadTools(new OldPluginGateway());
        var ex = await Assert.ThrowsAsync<McpException>(() => tools.Inspect());
        Assert.StartsWith("[PLUGIN_OUTDATED]", ex.Message);
        Assert.Contains("install_autocad_plugin.ps1", ex.Message);
    }
}
