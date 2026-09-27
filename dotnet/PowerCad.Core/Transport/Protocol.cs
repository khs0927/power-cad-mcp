using System.Text.Json.Nodes;

namespace PowerCad.Core;

/// <summary>
/// Wire protocol between the MCP server and the AutoCAD plugin: newline-delimited JSON over a
/// per-user named pipe. Request: {"id","token","command","params"}. Response: {"id","ok","result"} or
/// {"id","ok":false,"error":{"code","message","hint"}}.
/// </summary>
public static class Protocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 1 << 20; // 1 MiB; ask for narrower queries instead of bigger dumps

    public static JsonObject Request(string id, string token, string command, JsonObject? parameters) => new()
    {
        ["id"] = id,
        ["token"] = token,
        ["command"] = command,
        ["params"] = parameters ?? [],
    };

    public static JsonObject Success(string? id, JsonNode? result) => new()
    {
        ["id"] = id,
        ["ok"] = true,
        ["result"] = result,
    };

    public static JsonObject Failure(string? id, string code, string message, string? hint = null) => new()
    {
        ["id"] = id,
        ["ok"] = false,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["hint"] = hint },
    };

    public static JsonObject Failure(string? id, CadException e) => Failure(id, e.Code, e.Message, e.Hint);
}
