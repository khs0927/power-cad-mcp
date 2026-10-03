using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PowerCad.Server;
using Xunit;

namespace PowerCad.Tests;

public sealed class NvidiaVisualReasoningTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    [Fact]
    public async Task Cosmos_client_sends_image_and_discards_think_trace()
    {
        string? authorization = null;
        string? body = null;
        var handler = new StubHandler(request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var response = new JsonObject
            {
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["message"] = new JsonObject
                        {
                            ["content"] = "<think>private reasoning that must not persist</think>{\"scene_type\":\"floor_plan\",\"verdict\":\"PASS\",\"objects\":[],\"issues\":[]}"
                        }
                    }
                }
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json")
            };
        });
        var client = new NvidiaCosmosReasoner(
            new HttpClient(handler),
            new Uri("https://integrate.api.nvidia.com/v1/chat/completions"),
            "nvapi-test",
            "nvidia/cosmos-reason2-2b");

        var result = await client.AnalyzeAsync([1, 2, 3], "image/png", "inspect", CancellationToken.None);

        Assert.Equal("Bearer nvapi-test", authorization);
        Assert.Contains("data:image/png;base64,", body);
        Assert.Equal("floor_plan", result["scene_type"]!.GetValue<string>());
        Assert.DoesNotContain("private reasoning", result.ToJsonString());
    }

    [Fact]
    public void Final_parser_keeps_only_post_think_json()
    {
        var parsed = NvidiaCosmosReasoner.ParseFinalJson("<think>do not store me</think>\n```json\n{\"verdict\":\"REVIEW\"}\n```");
        Assert.Equal("REVIEW", parsed["verdict"]!.GetValue<string>());
        Assert.DoesNotContain("do not store me", parsed.ToJsonString());
    }
}
