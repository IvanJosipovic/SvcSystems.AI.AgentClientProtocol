using SvcSystems.AI.AgentClientProtocol;
using SvcSystems.AI.AgentClientProtocol.Protocol.Generated;
using System.Text.Json;
using Xunit;

namespace SvcSystems.AI.AgentClientProtocol.IntegrationTests;

public sealed class TestyIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task TestyEchoStreamsAnAgentMessage()
    {
        var binaries = await TestyBinaryProvider.GetAsync();
        if (binaries is null)
        {
            Assert.Skip("Testy auto-build requires Cargo. Install Rust/Cargo or set ACP_TESTY_BIN.");
        }
        var testyPath = binaries.TestyPath;

        var workingDirectory = Path.Combine(Path.GetTempPath(), "acp-testy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await using var client = await Client.StartAsync(
                new ProcessOptions
                {
                    FileName = testyPath,
                    WorkingDirectory = workingDirectory,
                    StandardErrorReceived = line => Console.Error.WriteLine("[testy] " + line)
                },
                cancellationToken: timeout.Token);

            Assert.NotNull(client.Agent);
            Assert.Equal(1, client.Agent.ProtocolVersion);

            var session = await client.CreateSessionAsync(
                new SessionOptions { WorkingDirectory = workingDirectory },
                timeout.Token);

            var updates = new List<SessionEvent>();
            await foreach (var update in session.PromptAsync("echo hello from acp client", timeout.Token))
            {
                updates.Add(update);
            }

            Assert.Contains(updates, update => update is ContentUpdate
            {
                Role: "agent",
                Content: PromptTextContent { Text: var text }
            } && text.Contains("hello from acp client", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(updates, update => update is Completed { Reason: "end_turn" });
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); }
            catch (IOException) { /* the agent process may still be shutting down */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup of a test-owned temp directory */ }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TestyWaitForCancelConfirmsPromptCancellation()
    {
        var binaries = await TestyBinaryProvider.GetAsync();
        if (binaries is null)
        {
            Assert.Skip("Testy auto-build requires Cargo. Install Rust/Cargo or set ACP_TESTY_BIN.");
        }
        var testyPath = binaries.TestyPath;

        var workingDirectory = Path.Combine(Path.GetTempPath(), "acp-testy-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await using var client = await Client.StartAsync(
                new ProcessOptions { FileName = testyPath, WorkingDirectory = workingDirectory },
                cancellationToken: timeout.Token);
            var session = await client.CreateSessionAsync(new SessionOptions { WorkingDirectory = workingDirectory }, timeout.Token);
            var updates = new List<SessionEvent>();
            var promptTask = Task.Run(async () =>
            {
                await foreach (var update in session.PromptAsync("wait_for_cancel", timeout.Token)) updates.Add(update);
            }, timeout.Token);

            await Task.Delay(TimeSpan.FromMilliseconds(300), timeout.Token);
            await session.CancelAsync(timeout.Token);
            await promptTask.WaitAsync(timeout.Token);
            Assert.Contains(updates, update => update is Completed { Reason: "cancelled" });
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenCodeHandshakeWorksWhenConfigured()
    {
        var openCodePath = Environment.GetEnvironmentVariable("ACP_OPENCODE_BIN");
        if (string.IsNullOrWhiteSpace(openCodePath) || !File.Exists(openCodePath))
        {
            Assert.Skip("Set ACP_OPENCODE_BIN to an OpenCode executable to run this optional compatibility check.");
        }

        var workingDirectory = Path.Combine(Path.GetTempPath(), "acp-opencode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await using var client = await Client.StartAsync(
                new ProcessOptions { FileName = openCodePath, Arguments = ["acp"], WorkingDirectory = workingDirectory },
                cancellationToken: timeout.Token);

            Assert.Equal(1, client.Agent.ProtocolVersion);
            Assert.False(string.IsNullOrWhiteSpace(client.Agent.Name));
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TestyCanUseConfiguredStdioMcpServer()
    {
        var binaries = await TestyBinaryProvider.GetAsync();
        if (binaries is null || string.IsNullOrWhiteSpace(binaries.McpEchoPath) || !File.Exists(binaries.McpEchoPath))
        {
            Assert.Skip("This scenario requires Cargo to build both binaries, or valid ACP_TESTY_BIN and ACP_MCP_ECHO_BIN paths.");
        }
        var testyPath = binaries.TestyPath;
        var mcpEchoPath = binaries.McpEchoPath!;

        var workingDirectory = Path.Combine(Path.GetTempPath(), "acp-testy-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await using var client = await Client.StartAsync(
                new ProcessOptions { FileName = testyPath, WorkingDirectory = workingDirectory },
                cancellationToken: timeout.Token);
            var session = await client.CreateSessionAsync(new SessionOptions
            {
                WorkingDirectory = workingDirectory,
                McpServers = [new StdioMcpServerOptions("echo", mcpEchoPath)]
            }, timeout.Token);

            var updates = new List<SessionEvent>();
            await foreach (var update in session.PromptAsync("{\"command\":\"list_tools\",\"server\":\"echo\"}", timeout.Token)) updates.Add(update);
            Assert.Contains(updates, update => update is ContentUpdate
            {
                Role: "agent",
                Content: PromptTextContent { Text: var text }
            } && text.Contains("Available tools", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TestyFullExercisesAdvertisedClientCapabilities()
    {
        var binaries = await TestyBinaryProvider.GetAsync();
        if (binaries is null)
        {
            Assert.Skip("Testy auto-build requires Cargo. Install Rust/Cargo or set ACP_TESTY_BIN.");
        }
        var testyPath = binaries.TestyPath;

        var workingDirectory = Path.Combine(Path.GetTempPath(), "acp-testy-full-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var fileSystem = new SandboxedFileSystemHandler(workingDirectory);
        var elicitationHandler = new DeterministicElicitationHandler();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await using var client = await Client.StartAsync(
                new ProcessOptions
                {
                    FileName = testyPath,
                    WorkingDirectory = workingDirectory
                },
                new ClientOptions
                {
                    PermissionHandler = new RejectOncePermissionHandler(),
                    FileSystemHandler = fileSystem,
                    TerminalHandler = new DeterministicTerminalHandler(),
                    ElicitationHandler = elicitationHandler,
                    ElicitationCompleted = elicitationHandler.OnCompleted
                },
                timeout.Token);

            await client.AuthenticateAsync("testy-agent-auth", timeout.Token);
            var extraDirectory = Path.Combine(workingDirectory, "extra");
            Directory.CreateDirectory(extraDirectory);
            var sessionOptions = new SessionOptions
            {
                WorkingDirectory = workingDirectory,
                AdditionalDirectories = [extraDirectory]
            };
            var session = await client.CreateSessionAsync(sessionOptions, timeout.Token);
            await client.LoadSessionAsync(session.Id, sessionOptions, timeout.Token);
            await session.SetModeAsync("plan", timeout.Token);
            await session.SetConfigOptionAsync("verbosity", "verbose", timeout.Token);
            Assert.Contains(await client.ListSessionsAsync(workingDirectory, cancellationToken: timeout.Token), listed => listed.SessionId == session.Id);
            await client.ResumeSessionAsync(session.Id, new SessionOptions { WorkingDirectory = workingDirectory }, timeout.Token);
            var updates = new List<SessionEvent>();
            await foreach (var update in session.PromptAsync("full", timeout.Token)) updates.Add(update);

            var report = updates.OfType<ContentUpdate>().FirstOrDefault(update =>
                update.Role == "agent" && update.Content is PromptTextContent text && text.Text.Contains("scenario: full", StringComparison.Ordinal));
            Assert.NotNull(report);
            var text = ((PromptTextContent)report.Content).Text;
            Assert.Contains("session/request_permission: ok", text, StringComparison.Ordinal);
            Assert.Contains("fs/write_text_file: ok", text, StringComparison.Ordinal);
            Assert.Contains("fs/read_text_file: ok", text, StringComparison.Ordinal);
            Assert.Contains("terminal/create: ok", text, StringComparison.Ordinal);
            Assert.Contains("terminal/output: ok", text, StringComparison.Ordinal);
            Assert.Contains("terminal/wait_for_exit: ok", text, StringComparison.Ordinal);
            Assert.Contains("terminal/kill: ok", text, StringComparison.Ordinal);
            Assert.Contains("terminal/release: ok", text, StringComparison.Ordinal);
            Assert.Contains("elicitation/form_session_accept: ok", text, StringComparison.Ordinal);
            Assert.Contains("elicitation/url_session_accept: ok", text, StringComparison.Ordinal);
            Assert.Contains("elicitation/complete_session_url: sent", text, StringComparison.Ordinal);
            Assert.True(elicitationHandler.Completed.Count > 0);
            Assert.Contains(updates, update => update is AgentToolCallUpdate);
            Assert.Contains(updates, update => update is PlanUpdate);
            Assert.Contains(updates, update => update is TokenUsageUpdate);
            await client.CloseSessionAsync(session.Id, timeout.Token);

            var deleteSession = await client.CreateSessionAsync(new SessionOptions { WorkingDirectory = workingDirectory }, timeout.Token);
            await client.DeleteSessionAsync(deleteSession.Id, timeout.Token);
            await client.LogoutAsync(timeout.Token);
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class RejectOncePermissionHandler : IPermissionHandler
    {
        public ValueTask<PermissionDecision> RequestPermissionAsync(PermissionRequest request, CancellationToken cancellationToken = default)
        {
            var reject = request.Options.First(option => option.Kind == "reject_once");
            return ValueTask.FromResult(PermissionDecision.Select(reject.Id));
        }
    }

    private sealed class SandboxedFileSystemHandler(string root) : IFileSystemHandler
    {
        public async ValueTask<string> ReadTextFileAsync(string sessionId, string path, uint? line, uint? limit, CancellationToken cancellationToken = default)
        {
            var safePath = ValidatePath(path);
            var content = await File.ReadAllTextAsync(safePath, cancellationToken);
            var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var lines = content.Split(["\r\n", "\n"], StringSplitOptions.None);
            var offset = Math.Max(0, checked((int)(line ?? 1) - 1));
            var count = limit is null ? int.MaxValue : checked((int)Math.Min(limit.Value, int.MaxValue));
            return string.Join(newline, lines.Skip(offset).Take(count));
        }

        public async ValueTask WriteTextFileAsync(string sessionId, string path, string content, CancellationToken cancellationToken = default)
        {
            var safePath = ValidatePath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(safePath)!);
            await File.WriteAllTextAsync(safePath, content, cancellationToken);
        }

        private string ValidatePath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Testy attempted to access a path outside its isolated workspace.");
            return fullPath;
        }
    }

    private sealed class DeterministicTerminalHandler : ITerminalHandler
    {
        private int _nextId;

        public ValueTask<string> CreateAsync(CreateTerminalRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult("testy-terminal-" + Interlocked.Increment(ref _nextId));

        public ValueTask<TerminalOutputResponse> GetOutputAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TerminalOutputResponse { Output = "testy terminal\n", Truncated = false, ExitStatus = new TerminalExitStatus { ExitCode = 0 } });

        public ValueTask<TerminalExitStatus> WaitForExitAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new TerminalExitStatus { ExitCode = 0 });

        public ValueTask KillAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask ReleaseAsync(string sessionId, string terminalId, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class DeterministicElicitationHandler : IElicitationHandler
    {
        public bool SupportsForm => true;
        public bool SupportsUrl => true;
        public List<string> Completed { get; } = [];

        public ValueTask<ElicitationResponse> RequestElicitationAsync(ElicitationRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Message.Contains("Decline", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult(new ElicitationResponse("decline"));
            if (request.Message.Contains("Cancel", StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult(new ElicitationResponse("cancel"));
            if (request.Mode == "form" && request.RequestedSchema is { } schema)
                return ValueTask.FromResult(new ElicitationResponse("accept", CreateFormAnswer(schema)));
            return ValueTask.FromResult(new ElicitationResponse("accept"));
        }

        public void OnCompleted(ElicitationCompleted notification) => Completed.Add(notification.ElicitationId);

        private static JsonElement CreateFormAnswer(JsonElement schema)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array &&
                    schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in required.EnumerateArray().Select(item => item.GetString()).Where(name => name is not null))
                    {
                        if (!properties.TryGetProperty(field!, out var definition)) continue;
                        writer.WritePropertyName(field!);
                        switch (definition.TryGetProperty("type", out var type) ? type.GetString() : null)
                        {
                            case "string": writer.WriteStringValue("testy"); break;
                            case "number": writer.WriteNumberValue(0.5); break;
                            case "integer": writer.WriteNumberValue(42); break;
                            case "boolean": writer.WriteBooleanValue(true); break;
                            case "array": writer.WriteStartArray(); writer.WriteEndArray(); break;
                            default: writer.WriteNullValue(); break;
                        }
                    }
                }
                writer.WriteEndObject();
            }
            return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
        }
    }
}
