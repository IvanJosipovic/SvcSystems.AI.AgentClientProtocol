using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SvcSystems.AI.AgentClientProtocol;
using SvcSystems.AI.AgentClientProtocol.Transport;
using AcpClient = SvcSystems.AI.AgentClientProtocol.Client.Client;

internal static class ExtensionSmokeTest
{
    internal static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var metadata = JsonDocument.Parse("""{"smoke":true}""");
        var values = new Dictionary<string, JsonElement> { ["smoke"] = metadata.RootElement.GetProperty("smoke").Clone() };
        var notifications = 0;
        var statuses = 0;
        await using var transport = new SmokeTransport();
        await using var client = await AcpClient.ConnectAsync(transport, new ClientOptions
        {
            Metadata = values,
            CapabilityMetadata = values,
            AuthenticationStatusChanged = _ => statuses++,
            ExtensionNotificationReceived = _ => notifications++,
            ExtensionRequestHandlers = new Dictionary<string, Func<JsonElement, CancellationToken, ValueTask<JsonElement>>>
            {
                ["_smoke/host"] = (parameters, _) => ValueTask.FromResult(parameters)
            }
        }, timeout.Token);
        var session = await client.CreateSessionAsync(new SessionOptions
        {
            WorkingDirectory = Environment.CurrentDirectory,
            Metadata = values
        }, timeout.Token);
        await foreach (var _ in session.PromptAsync("smoke", timeout.Token, new PromptOptions { Metadata = values })) { }
        var result = await client.SendExtensionRequestAsync("_smoke/echo", metadata.RootElement, timeout.Token);
        await client.SendExtensionNotificationAsync("_smoke/event", metadata.RootElement, timeout.Token);
        if (result.GetProperty("smoke").GetBoolean() != true ||
            client.AuthenticationStatus?.Kind != "account" || statuses != 1 || notifications != 2 ||
            session.PromptSuggestions is not ["Continue"] || !transport.HostResponseReceived || !transport.NotificationReceived)
            throw new InvalidOperationException("Extension smoke test failed.");
        Console.WriteLine("Extension smoke test passed.");
    }

    private sealed class SmokeTransport : ITransport
    {
        private readonly Channel<ReadOnlyMemory<byte>> _messages = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        public bool HostResponseReceived { get; private set; }
        public bool NotificationReceived { get; private set; }

        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var method))
            {
                HostResponseReceived = root.GetProperty("id").GetString() == "host-1" &&
                    root.GetProperty("result").GetProperty("smoke").GetBoolean();
                return ValueTask.CompletedTask;
            }
            var parameters = root.GetProperty("params");
            if (method.GetString() == "_smoke/event")
            {
                NotificationReceived = parameters.GetProperty("smoke").GetBoolean();
                return ValueTask.CompletedTask;
            }
            var id = root.GetProperty("id").GetRawText();
            switch (method.GetString())
            {
                case "initialize":
                    RequireMetadata(parameters);
                    RequireMetadata(parameters.GetProperty("clientCapabilities"));
                    Push("""{"jsonrpc":"2.0","method":"_auth/status_update","params":{"authStatus":{"kind":"account","label":"Smoke account"}}}""");
                    Reply(id, """{"protocolVersion":1,"agentCapabilities":{"_meta":{"authStatus":{}}}}""");
                    break;
                case "session/new":
                    RequireMetadata(parameters);
                    Reply(id, """{"sessionId":"smoke-session"}""");
                    break;
                case "session/prompt":
                    RequireMetadata(parameters);
                    Push("""{"jsonrpc":"2.0","method":"_smoke/status","params":{"smoke":true}}""");
                    Push("""{"jsonrpc":"2.0","id":"host-1","method":"_smoke/host","params":{"smoke":true}}""");
                    Push("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"smoke-session","update":{"sessionUpdate":"session_info_update","_meta":{"promptSuggestions":{"suggestions":[{"text":"Continue"}]}}}}}""");
                    Reply(id, """{"stopReason":"end_turn"}""");
                    break;
                case "_smoke/echo":
                    Reply(id, parameters.GetRawText());
                    break;
                default:
                    throw new InvalidOperationException("Unexpected smoke-test method.");
            }
            return ValueTask.CompletedTask;
        }

        private static void RequireMetadata(JsonElement parameters)
        {
            if (!parameters.GetProperty("_meta").GetProperty("smoke").GetBoolean())
                throw new InvalidOperationException("Extension metadata was lost.");
        }

        private void Reply(string id, string result) =>
            Push($$"""{"jsonrpc":"2.0","id":{{id}},"result":{{result}}}""");

        private void Push(string message)
        {
            if (!_messages.Writer.TryWrite(Encoding.UTF8.GetBytes(message)))
                throw new InvalidOperationException("Smoke-test transport is closed.");
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var message in _messages.Reader.ReadAllAsync(cancellationToken)) yield return message;
        }

        public ValueTask DisposeAsync()
        {
            _messages.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
