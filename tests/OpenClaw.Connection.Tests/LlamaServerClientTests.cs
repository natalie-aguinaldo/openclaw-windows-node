using OpenClaw.Connection.LocalAi;
using System.Net;
using System.Text;

namespace OpenClaw.Connection.Tests;

public sealed class LlamaServerClientTests
{
    private const string Alias = "qwen3.6-35b-a3b-mtp-q4-k-m";
    private static readonly Uri Endpoint = new("http://127.0.0.1:28765/v1");

    [Fact]
    public async Task ProbeRouterAsync_ModelStatusTimeout_ReturnsNotHealthy()
    {
        using var client = new LlamaServerClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/models"
                ? throw new OperationCanceledException()
                : JsonResponse("""{"status":"ok"}""")));

        LlamaServerRouterProbeResult result = await client.ProbeRouterAsync(
            Endpoint,
            Alias,
            ManagedModelPath());

        Assert.False(result.IsHealthy);
        Assert.False(result.IsReady);
        Assert.Equal(LocalAiModelAvailabilityState.Unknown, result.ModelState);
    }

    [Fact]
    public async Task ProbeRouterAsync_MissingConfiguredAlias_ReturnsNotHealthy()
    {
        using var client = new LlamaServerClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/models"
                ? JsonResponse(ModelListJson("other-model", "unloaded", ManagedModelPath()))
                : JsonResponse("""{"status":"ok"}""")));

        LlamaServerRouterProbeResult result = await client.ProbeRouterAsync(
            Endpoint,
            Alias,
            ManagedModelPath());

        Assert.False(result.IsHealthy);
        Assert.False(result.IsReady);
        Assert.Equal(LocalAiModelAvailabilityState.NotInstalled, result.ModelState);
    }

    [Fact]
    public async Task ProbeRouterAsync_InvalidModelsResponse_ReturnsNotHealthy()
    {
        using var client = new LlamaServerClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/models"
                ? JsonResponse("""{"data":{}}""")
                : JsonResponse("""{"status":"ok"}""")));

        LlamaServerRouterProbeResult result = await client.ProbeRouterAsync(
            Endpoint,
            Alias,
            ManagedModelPath());

        Assert.False(result.IsHealthy);
        Assert.False(result.IsReady);
        Assert.Equal(LocalAiModelAvailabilityState.Unknown, result.ModelState);
    }

    [Fact]
    public async Task ProbeRouterAsync_UnknownModelState_ReturnsNotHealthy()
    {
        using var client = new LlamaServerClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/models"
                ? JsonResponse(ModelListJson(Alias, "error", ManagedModelPath()))
                : JsonResponse("""{"status":"ok"}""")));

        LlamaServerRouterProbeResult result = await client.ProbeRouterAsync(
            Endpoint,
            Alias,
            ManagedModelPath());

        Assert.False(result.IsHealthy);
        Assert.False(result.IsReady);
        Assert.Equal(LocalAiModelAvailabilityState.Unknown, result.ModelState);
        Assert.Equal(ManagedModelPath(), result.ReportedModelPath);
    }

    [Fact]
    public async Task ProbeRouterAsync_WrongModelPath_ReturnsNotHealthy()
    {
        using var client = new LlamaServerClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/models"
                ? JsonResponse(ModelListJson(Alias, "unloaded", ManagedModelPath("wrong.gguf")))
                : JsonResponse("""{"status":"ok"}""")));

        LlamaServerRouterProbeResult result = await client.ProbeRouterAsync(
            Endpoint,
            Alias,
            ManagedModelPath());

        Assert.False(result.IsHealthy);
        Assert.False(result.IsReady);
        Assert.Equal(LocalAiModelAvailabilityState.Unknown, result.ModelState);
    }

    [Theory]
    [InlineData("unloaded", LocalAiModelAvailabilityState.Verified)]
    [InlineData("loaded", LocalAiModelAvailabilityState.Loaded)]
    public async Task ProbeRouterAsync_VerifiedManagedModel_ReturnsHealthy(string serverStatus, LocalAiModelAvailabilityState expectedState)
    {
        using var client = new LlamaServerClient(new StubHandler(request =>
            request.RequestUri!.AbsolutePath == "/models"
                ? JsonResponse(ModelListJson(Alias, serverStatus, ManagedModelPath()))
                : JsonResponse("""{"status":"ok"}""")));

        LlamaServerRouterProbeResult result = await client.ProbeRouterAsync(
            Endpoint,
            Alias,
            ManagedModelPath());

        Assert.True(result.IsHealthy);
        Assert.True(result.IsReady);
        Assert.Equal(expectedState, result.ModelState);
        Assert.Equal(ManagedModelPath(), result.ReportedModelPath);
    }

    private static string ModelListJson(string alias, string status, string modelPath) =>
        $$"""
        {
          "data": [
            {
              "id": "{{alias}}",
              "status": {
                "value": "{{status}}",
                "args": ["--model", "{{JsonEscape(modelPath)}}"]
              }
            }
          ]
        }
        """;

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static string ManagedModelPath(string fileName = "Qwen3.6-35B-A3B-UD-Q4_K_M.gguf") =>
        Path.Combine("C:\\", "OpenClaw", "models", fileName);

    private static string JsonEscape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
