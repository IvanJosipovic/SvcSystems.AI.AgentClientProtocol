using System.Net;
using System.Text;
using SvcSystems.AI.AgentClientProtocol.Registry;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.Tests;

public sealed class RegistryClientTests
{
    [Fact]
    public async Task GetIndexAsyncSendsGetToConfiguredUriAndDeserializesResponse()
    {
        var indexUri = new Uri("https://registry.example.test/v1/index.json");
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse("""
            {
              "version": "1.2.3",
              "agents": [
                {
                  "id": "sample-agent",
                  "name": "Sample Agent",
                  "version": "2.0.1",
                  "description": "A test agent",
                  "license_url": "https://example.test/license",
                  "distribution": { "npx": { "package": "@example/sample-agent", "args": ["--quiet"] } },
                  "preview": { "version": "2.1.0-preview.1", "distribution": { "uvx": { "package": "sample-agent" } } }
                }
              ]
            }
            """)));
        using var httpClient = new HttpClient(handler);
        using var registry = new RegistryClient(httpClient, indexUri);

        var index = await registry.GetIndexAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Equal(indexUri, handler.Request.RequestUri);
        Assert.Equal("1.2.3", index.Version);
        var agent = Assert.Single(index.Agents);
        Assert.Equal("sample-agent", agent.Id);
        Assert.Equal("https://example.test/license", agent.LicenseUrl);
        Assert.Equal("@example/sample-agent", agent.Distribution.Npx!.Package);
        Assert.Equal(["--quiet"], agent.Distribution.Npx.Args);
        Assert.Equal("2.1.0-preview.1", agent.Preview!.Version);
    }

    [Fact]
    public async Task GetIndexAsyncThrowsForUnsuccessfulResponse()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var httpClient = new HttpClient(handler);
        using var registry = new RegistryClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => registry.GetIndexAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    [Fact]
    public async Task GetIndexAsyncThrowsForInvalidJson()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse("not json")));
        using var httpClient = new HttpClient(handler);
        using var registry = new RegistryClient(httpClient);

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(
            () => registry.GetIndexAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DisposeDoesNotDisposeCallerOwnedHttpClient()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(JsonResponse("{}")));
        using var httpClient = new HttpClient(handler);
        var registry = new RegistryClient(httpClient);

        registry.Dispose();

        Assert.False(handler.IsDisposed);
    }

    [Fact]
    public void CanCreateAndDisposeClientWithoutProvidingHttpClient()
    {
        using var registry = new RegistryClient();

        Assert.NotNull(registry);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return sendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
