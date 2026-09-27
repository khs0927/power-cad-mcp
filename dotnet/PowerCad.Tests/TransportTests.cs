using System.Text.Json.Nodes;
using PowerCad.Core;
using PowerCad.Core.Commands;
using PowerCad.Core.Simulation;
using PowerCad.Core.Transport;
using Xunit;

namespace PowerCad.Tests;

public sealed class TransportTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("powercad-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    internal static (PipeServer Server, DiscoveryInfo Info) StartFakePlugin(string home, InMemoryCadDocument doc, int pid)
    {
        var info = new DiscoveryInfo(1, "autocad", 2027, $"powercad-test-{Guid.NewGuid():N}", DiscoveryStore.NewToken(), pid, "0.2.0", DateTimeOffset.UtcNow);
        var dispatcher = new CommandDispatcher(doc);
        var server = new PipeServer(info.PipeName, info.AuthToken, (c, p, ct) => Task.Run(() => dispatcher.Execute(c, p), ct));
        server.Start();
        new DiscoveryStore(home).Write(info);
        return (server, info);
    }

    [Fact]
    public async Task Pipe_roundtrip_with_token_auth()
    {
        var (server, info) = StartFakePlugin(_home, InMemoryCadDocument.CreateSample(), Environment.ProcessId);
        await using (server)
        {
            await using var client = new PipeClient(info, TimeSpan.FromSeconds(10));
            var status = await client.SendAsync("status", null);
            Assert.Equal("simulator", status!["backend"]!.GetValue<string>());

            var q = await client.SendAsync("query", new JsonObject { ["types"] = new JsonArray("INSERT") });
            Assert.Equal(2, q!["total"]!.GetValue<int>());

            var err = await Assert.ThrowsAsync<CadException>(() => client.SendAsync("move", new JsonObject { ["handles"] = new JsonArray("FFFF"), ["displacement"] = new JsonArray(1, 0) }));
            Assert.Equal(ErrorCodes.NotFound, err.Code);
            Assert.NotNull(err.Hint);

            await using var intruder = new PipeClient(info with { AuthToken = "wrong" }, TimeSpan.FromSeconds(10));
            var denied = await Assert.ThrowsAsync<CadException>(() => intruder.SendAsync("status", null));
            Assert.Equal(ErrorCodes.Unauthorized, denied.Code);
        }
    }

    [Fact]
    public async Task Server_rejects_malformed_and_oversized_requests()
    {
        await using var server = new PipeServer("unused", "t", (c, p, ct) => Task.FromResult<JsonNode>(new JsonObject()));
        var ct = CancellationToken.None;
        Assert.Equal(ErrorCodes.InvalidParams, (await server.HandleLineAsync("not json", ct))["error"]!["code"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.InvalidParams, (await server.HandleLineAsync(new string('x', Protocol.MaxMessageBytes + 1), ct))["error"]!["code"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.InvalidParams, (await server.HandleLineAsync("""{"token":"t"}""", ct))["error"]!["code"]!.GetValue<string>());
        Assert.True((await server.HandleLineAsync("""{"token":"t","command":"x","id":"1"}""", ct))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Unexpected_errors_are_sanitized()
    {
        await using var server = new PipeServer("unused", "t", (c, p, ct) => throw new InvalidOperationException(@"boom in C:\Users\kim\secret\plan.dwg"));
        var r = await server.HandleLineAsync("""{"token":"t","command":"x"}""", CancellationToken.None);
        var message = r["error"]!["message"]!.GetValue<string>();
        Assert.Equal(ErrorCodes.Internal, r["error"]!["code"]!.GetValue<string>());
        Assert.DoesNotContain("kim", message);
        Assert.Contains("<path>", message);
    }

    [Fact]
    public void Discovery_lists_live_plugins_and_removes_orphans()
    {
        var store = new DiscoveryStore(_home);
        var alive = new DiscoveryInfo(1, "autocad", 2027, "p1", "t1", 111, "0.2.0", DateTimeOffset.UtcNow);
        var dead = alive with { Pid = 222, PipeName = "p2", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        store.Write(alive);
        store.Write(dead);
        File.WriteAllText(Path.Combine(_home, "garbage.json"), "{not json");

        var live = store.List(pid => pid == 111);
        Assert.Single(live);
        Assert.Equal("autocad-2027-111", live[0].Target);
        Assert.False(File.Exists(store.PathFor(dead)));
        Assert.False(File.Exists(Path.Combine(_home, "garbage.json")));
        store.Delete(alive);
        Assert.Empty(store.List(_ => true));
    }
}
