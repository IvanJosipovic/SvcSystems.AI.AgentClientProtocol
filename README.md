# SvcSystems.AI.AgentClientProtocol

[![NuGet](https://img.shields.io/nuget/v/SvcSystems.AI.AgentClientProtocol)](https://www.nuget.org/packages/SvcSystems.AI.AgentClientProtocol)

A .NET library for the [Agent Client Protocol (ACP)](https://agentclientprotocol.com/). The core package provides both a session-oriented client and an agent host in the `SvcSystems.AI.AgentClientProtocol.Agent` namespace.

Targets `net11.0`. The library is trim-analyzed and NativeAOT compatible. Reflection-based `System.Text.Json` serialization is disabled in the project.

## Quick start

```csharp
using SvcSystems.AI.AgentClientProtocol;
using AcpClient = SvcSystems.AI.AgentClientProtocol.Client.Client;

await using var client = await AcpClient.StartAsync(
    new ProcessOptions
    {
        FileName = "opencode",
        Arguments = ["acp"],
        WorkingDirectory = @"C:\repos\MyProject"
    },
    new ClientOptions
    {
        PermissionHandler = new ConsolePermissionHandler()
    });

var session = await client.CreateSessionAsync(new SessionOptions
{
    WorkingDirectory = @"C:\repos\MyProject"
});

await foreach (var update in session.PromptAsync("Explain this repository"))
{
    Console.WriteLine(update);
}
```

`Client.StartAsync` starts the child process and completes ACP initialization before returning. The client owns and disposes the process. Standard output is reserved for ACP frames; standard error is delivered through `StandardErrorReceived`.

## Hosting an agent

Implement `IAcpAgentHandler` for
initialize, session creation, and prompt handling. `AcpAgent` uses the current process's stdin/stdout
by default, or accepts a custom `ITransport` for embedding and tests.

The handler also exposes optional callbacks for session load/resume/list/delete/close, mode and
configuration changes, authentication, and logout. Unimplemented optional callbacks return
"Method not found"; advertise only the capabilities your handler supports.

```csharp
using SvcSystems.AI.AgentClientProtocol.Agent;

await using var agent = new AcpAgent(new MyAgentHandler());
await agent.RunAsync(cancellationToken);
```

Prompt callbacks receive an `IAcpAgentContext` to stream `session/update` messages and make client
requests. The runtime dispatches requests concurrently within a configured bound, so
`session/cancel` can be handled while a prompt callback is active.

A dropped transport or protocol failure completes `Completion` with the failure and raises `TransportFaulted` once; a clean shutdown completes with `null` and raises nothing. `TransportFaulted` is a member of `IClient`, so external implementers of the interface must add it.

Unrecognized JSON-RPC notifications, including agent-specific extensions such as `_auth/status_update`, are ignored without closing the connection. Unsupported requests still receive a JSON-RPC "Method not found" error.

## Host capabilities

ACP callbacks are enabled only when the host provides a handler. The client can host filesystem, terminal, permission, and elicitation operations. Filesystem callbacks are restricted to each active session's working directory and explicitly configured additional directories; paths that resolve through symlinks or reparse points outside those roots are rejected. Filesystem handlers should still enforce their own authorization and operation-specific policy. Do not configure terminal handlers unless the application has an appropriate workspace/command security policy.

```csharp
var clientOptions = new ClientOptions
{
    PermissionHandler = permissionHandler,
    FileSystemHandler = workspaceFileSystem,
    TerminalHandler = sandboxedTerminal,
    ElicitationHandler = userElicitation
};
```

The client advertises only the capabilities supported by these configured handlers. `Session` exposes prompt streaming, cancellation, mode changes, and session configuration. `IClient` exposes create/load/list/resume/delete/close, authentication, and logout operations, with optional operations gated by the agent's negotiated capabilities.

ACP model types are generated from the checked-in schema and exposed directly: `Client.Agent` is an `InitializeResponse`, and MCP servers, prompt content, session updates, and config options use their generated wire models. Prompt streams yield `SessionUpdateEvent` values containing the generated `SessionUpdate` union and `Completed` values containing the generated `PromptResponse`; unknown update, content, and config-option variants remain available as `JsonElement`.

## Adapter extensions and metadata

Use the generic extension APIs for adapter-specific methods. They preserve trim safety; capability
checks and authorization remain the application's responsibility.

```csharp
var clientOptions = new ClientOptions
{
    ExtensionNotificationReceived = notification =>
        Console.WriteLine($"Adapter notification: {notification.Method}"),
};

using var parameters = JsonDocument.Parse("""{"sessionId":"session-id","title":"New title"}""");
var response = await client.SendExtensionRequestAsync("_session/rename", parameters.RootElement);
await client.SendExtensionNotificationAsync("_example/event", parameters.RootElement);
```

Register incoming request handlers in `ClientOptions.ExtensionRequestHandlers` before connecting.
Names must start with `_`; unhandled requests receive “Method not found.” Callbacks run on the
receive loop and must not block on ACP operations or disposal. Callback exceptions fail the
connection. Outgoing names may also target legacy methods; these low-level calls do not perform
capability checks or session bookkeeping.

`_auth/status_update` is available as `client.AuthenticationStatus`; prompt suggestions are
available on session updates and `session.PromptSuggestions`. These are optional adapter
extensions, not authentication proof or ACP guarantees.

`ClientOptions.Metadata` and `CapabilityMetadata` attach `_meta` to `initialize` and
`clientCapabilities`; `SessionOptions.Metadata` and `PromptOptions.Metadata` attach it to
session and prompt requests. Other extension calls can include `_meta` directly in their JSON
parameters. Incoming raw payloads retain metadata and unknown fields.

## Schema-generated wire models

The incremental Roslyn generator builds typed wire models and trim-safe JSON writers from the
checked-in ACP v1 schema. Open payloads such as `_meta` remain `JsonElement`-based; the library
does not use reflection-based `JsonSerializer` overloads.

A tagged-union branch that declares neither a `const` discriminator value nor an inferable title
is reported as `ACPGEN003` and fails the build, rather than silently dropping that case from the
generated union. A schema whose naming collides, or whose generator options and metadata files are
missing, invalid, or ambiguous, is likewise reported (`ACPGEN001`–`ACPGEN008`).

## ACP Registry

`RegistryClient` fetches the registry index using a caller-managed `HttpClient`; pass an
alternate absolute URI to read from a mirror or test server. The registry wire models and JSON
metadata are generated from `schema/registry/v1`.

```csharp
using SvcSystems.AI.AgentClientProtocol.Registry;

var registry = new RegistryClient(httpClient);
var index = await registry.GetIndexAsync(cancellationToken);
```

## Dependency injection and logging

`AddAgentClientProtocol` registers an `IAcpClientFactory` and an `IRegistryClient`; existing
registrations are preserved, so the call is safe to repeat.

```csharp
using SvcSystems.AI.AgentClientProtocol;

services.AddAgentClientProtocol();
// Or point the registry client at a mirror:
services.AddAgentClientProtocol(
    registryIndexUri: new Uri("https://mirror.example/registry.json"));
```

Both services are singletons, and a `defaultClientOptions` factory (when supplied) is invoked once
from the container root, so it must not depend on scoped services. When `defaultClientOptions` is
null, the factory builds options that report through the container's `ILoggerFactory`; pass your
own factory to replace those defaults entirely.

```csharp
public sealed class AgentRunner(IAcpClientFactory clients)
{
    public Task<IClient> StartAsync(CancellationToken cancellationToken) =>
        clients.StartAsync(
            new ProcessOptions { FileName = "opencode", Arguments = ["acp"] },
            cancellationToken: cancellationToken);
}
```

Set `ClientOptions.LoggerFactory` to receive structured diagnostics for connection and callback
failures. When it is null, diagnostics are written through `System.Diagnostics.Trace`; the
`RequestHandlerException` callback still takes precedence when it is set. The library targets the
Microsoft.Extensions abstractions that ship with the runtime, so registration and logging add no
package dependency.

## Trimming and NativeAOT

The library project enables AOT and trim analysis and sets:

```xml
<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
```

The console sample is the NativeAOT/trimmed consumer smoke test:

```powershell
dotnet publish samples/SvcSystems.AI.AgentClientProtocol.Console `
  -f net11.0 -r win-x64 --self-contained true `
  -p:PublishAot=true -p:PublishTrimmed=true
```

CI also publishes the sample for `net11.0` on Linux.

Run the published console executable with `--extension-smoke-test` to exercise metadata, incoming and outgoing extension requests/notifications, authentication status, and prompt suggestions without an external agent:

```powershell
& .\samples\SvcSystems.AI.AgentClientProtocol.Console\bin\Release\net11.0\win-x64\publish\SvcSystems.AI.AgentClientProtocol.Console.exe --extension-smoke-test
```

## Testing

- `SvcSystems.AI.AgentClientProtocol.Tests` tests JSON-RPC correlation, cancellation, error responses, and reflection-disabled execution.
- `SvcSystems.AI.AgentClientProtocol.Generator.Tests` tests the incremental generator against arbitrary JSON Schema inputs and checks that every applicable ACP method definition gets generated wire models.
- `SvcSystems.AI.AgentClientProtocol.IntegrationTests` launches the official deterministic **Testy** agent from the ACP Rust SDK. The `full` scenario exercises stable v1 updates and reverse callbacks without a live model or credentials.

The first Testy integration run downloads the pinned ACP Rust SDK source archive and builds `testy` and `mcp-echo-server` with Cargo. The source and binaries are cached in the user's local application-data directory. Rust/Cargo and network access are needed on the first run. To use prebuilt binaries, set `ACP_TESTY_BIN` and `ACP_MCP_ECHO_BIN` instead:

```powershell
git clone https://github.com/agentclientprotocol/rust-sdk.git rust-sdk
git -C rust-sdk checkout f5c0eef0972bbda8e75a151aeb088642911b3568
cargo build --manifest-path rust-sdk/Cargo.toml -p agent-client-protocol-test --bins
$env:ACP_TESTY_BIN = (Resolve-Path rust-sdk/target/debug/testy.exe)
$env:ACP_MCP_ECHO_BIN = (Resolve-Path rust-sdk/target/debug/mcp-echo-server.exe)
dotnet test tests/SvcSystems.AI.AgentClientProtocol.IntegrationTests --framework net11.0
```

If Cargo is unavailable, Testy tests are reported as skipped unless the binary paths are configured. Set `ACP_TESTY_AUTO_DOWNLOAD=false` to disable automatic provisioning. The OpenCode handshake check is opt-in with `ACP_OPENCODE_BIN`. The normal unit and generator tests do not require Rust or agent credentials.

### ACP TCK

[ACP TCK](https://github.com/agentclientprotocol/acp-tck) tests agents, not clients. We run its
complete v1 suite against official reference agents, plus a separate .NET client interoperability
suite against the same fixtures. These checks do not claim ACP TCK certification of this client.

```powershell
pwsh -File .\tools\Test-AcpTck.ps1
```

Requires Git, [uv](https://docs.astral.sh/uv/), and the .NET SDK in `global.json`. The first
run checks out a pinned TCK revision and provisions Python 3.14; no model, credentials, or
Cargo are needed. Reports and sources go under `artifacts/acp-tck`.

The upstream run covers all **56 registered v1 requirements** across minimal, authenticated,
and full-capability fixtures; the runner fails if any requirement lacks a passing result.
Client tests cover v1 sessions, prompts, capabilities, metadata, extensions, transport, and
error handling. GitHub Actions runs both suites on Windows and Linux.

ACP v2 is not supported (the client test verifies that a v2-only agent is rejected). TCK does
not cover positive MCP, filesystem, or terminal operations; those remain in the separate Testy
integration tests.

## Scope

The initial transport is stdio. The library targets stable ACP v1 and keeps the JSON-RPC implementation and generated wire types internal. HTTP/WebSocket transports and agent-specific APIs are not included.

## Future work

Not implemented yet:

- An agent-side runtime host (the mirror of this client) shipped as a separate package.
- Registry-driven discovery and launching of installed ACP agents.

## License

MIT. See [LICENSE](LICENSE).
