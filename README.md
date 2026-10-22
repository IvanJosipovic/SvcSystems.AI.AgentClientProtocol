# SvcSystems.AI.AgentClientProtocol

[![NuGet](https://img.shields.io/nuget/v/SvcSystems.AI.AgentClientProtocol)](https://www.nuget.org/packages/SvcSystems.AI.AgentClientProtocol)

A .NET library for the [Agent Client Protocol (ACP)](https://agentclientprotocol.com/): a
session-oriented client and an agent host.

Targets `net11.0`. Trim-analyzed and NativeAOT compatible.

## Client

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
    new ClientOptions { PermissionHandler = new ConsolePermissionHandler() });

var session = await client.CreateSessionAsync(new SessionOptions
{
    WorkingDirectory = @"C:\repos\MyProject"
});

await foreach (var update in session.PromptAsync("Explain this repository"))
{
    Console.WriteLine(update);
}
```

`StartAsync` completes the ACP handshake before returning and owns the child process. Standard
output carries ACP frames only; standard error arrives through `ProcessOptions.StandardErrorReceived`.

`Session` covers prompt streaming, cancellation, mode changes, and session configuration.
`IClient` covers create/load/list/resume/delete/close, authentication, and logout; optional
operations require the agent to advertise the matching capability.

## Agent host

Implement `IAgentHandler` for initialize, session creation, and prompt handling; the remaining
callbacks are optional and answer "Method not found" when unimplemented.

```csharp
using SvcSystems.AI.AgentClientProtocol.Agent;

await using var agent = new Agent(new MyAgentHandler());
await agent.RunAsync(cancellationToken);
```

`Agent` uses the current process's stdin/stdout by default, or accepts a custom `ITransport`.
Prompt callbacks receive an `IAgentContext` to stream `session/update` messages and call the
client through typed helpers (`ReadTextFileAsync`, `RequestPermissionAsync`, and the rest of
`ClientRequestExtensions`).

## Host capabilities

The client advertises only the capabilities its configured handlers support:

```csharp
var clientOptions = new ClientOptions
{
    PermissionHandler = permissionHandler,
    FileSystemHandler = workspaceFileSystem,
    TerminalHandler = sandboxedTerminal,
    ElicitationHandler = userElicitation
};
```

| Handler | Scope |
|---|---|
| Filesystem | Restricted to each session's working directory and configured additional directories. Paths resolving through symlinks or reparse points outside those roots are rejected. The handler receives the resolved path. |
| Terminal | The command must be permitted by the security policy and name a live session. The handler owns the remaining command, argument, and directory policy. |
| Permission | A request must reference a session the client tracks; unknown sessions are rejected. |

Filesystem and terminal handlers should still enforce their own authorization.

## Security policy

The agent is the untrusted peer. `ClientOptions.SecurityPolicy` decides what it may ask the host
to run or reach. It denies everything by default, so a host opts in explicitly:

```csharp
var clientOptions = new ClientOptions
{
    SecurityPolicy = new AcpSecurityPolicy
    {
        McpCommandAllowed = command => command == "/opt/mcp/my-server",
        McpEndpointAllowed = AcpSecurityPolicy.AllowPublicHttpsMcpEndpoints(),
        TerminalCommandAllowed = (command, args) => command == "git"
    }
};
```

| Policy | Default | Covers |
|---|---|---|
| `McpCommandAllowed` | Deny | The executable an MCP stdio server is launched with. |
| `McpEndpointAllowed` | Deny | The HTTP or SSE URL an MCP server is reached at. Only `http` and `https` are accepted. |
| `TerminalCommandAllowed` | Deny | The command an agent asks the host to run. |

`AllowPublicHttpsMcpEndpoints` permits HTTPS endpoints that are not loopback, link-local, or
private addresses, which keeps cloud instance metadata and internal services out of reach.

Other client-side guarantees: terminal output is truncated to the requested byte limit, a
terminal request must name a live session, the number of live sessions is capped, and agent
authentication environment variables are passed separately so the host can decline them.

## Failure handling

| Situation | Behavior |
|---|---|
| Transport or protocol failure | `Completion` completes with the failure and `TransportFaulted` raises once. |
| Clean shutdown | `Completion` completes with `null`; no event. |
| Unrecognized or malformed notification | Logged and skipped; the connection stays up. |
| Unparseable frame | Terminates the connection with a protocol failure. |
| Request the client does not implement | Answered with "Method not found". |
| Request whose handler rejects it | Answered with the handler's error code. |

## Extensions and metadata

Extension methods use the generic APIs and stay trim-safe; capability checks and authorization
remain the application's responsibility.

```csharp
var clientOptions = new ClientOptions
{
    ExtensionNotificationReceived = n => Console.WriteLine($"Adapter notification: {n.Method}"),
};

using var parameters = JsonDocument.Parse("""{"sessionId":"session-id","title":"New title"}""");
var response = await client.SendExtensionRequestAsync("_session/rename", parameters.RootElement);
await client.SendExtensionNotificationAsync("_example/event", parameters.RootElement);
```

Incoming request handlers go in `ClientOptions.ExtensionRequestHandlers` before connecting. Names
must start with `_`. Callbacks run on the receive loop and must not block on ACP operations.

`_meta` attaches through `ClientOptions.Metadata`, `CapabilityMetadata`, `SessionOptions.Metadata`,
and `PromptOptions.Metadata`. `Session.RawPayload` and `AuthenticationStatus.RawPayload` expose
vendor-specific data on otherwise typed payloads.

`AcpJson` exposes the generated serializer contract (`Options`, `TypeInfo<T>()`, `SerializeToElement`,
`SerializeToUtf8Bytes`, `Deserialize<T>`) so extension payloads use the same wire settings.

## Registry

`RegistryClient` reads the registry index over a caller-managed `HttpClient`.
`RegistryAgentLauncher` turns an entry into `ProcessOptions`, preferring `npx`, then `uvx`, then a
platform binary. Archives are downloaded over HTTPS, verified against the published SHA-256 when
one exists, and rejected if they contain links or escape the cache directory.

```csharp
using var launcher = new RegistryAgentLauncher(httpClient, new RegistryAgentLauncherOptions
{
    AgentsDirectory = Path.Combine(appData, "acp-agents"),
    RequireChecksum = true
});
var processOptions = await launcher.BuildProcessOptionsAsync(agent, workingDirectory, cancellationToken);
```

## Dependency injection

```csharp
services.AddAgentClientProtocol();
services.AddAgentClientProtocol(registryIndexUri: new Uri("https://mirror.example/registry.json"));
```

Registers a singleton `IAcpClientFactory` and `IRegistryClient`. A `defaultClientOptions` factory
is invoked once from the container root, so it must not depend on scoped services. Set
`ClientOptions.LoggerFactory` for structured diagnostics; without it they go to `Trace`.

## Trimming and NativeAOT

Reflection-based serialization is disabled; all wire models are generated from the checked-in
schema. The console sample is the AOT smoke test:

```powershell
dotnet publish samples/SvcSystems.AI.AgentClientProtocol.Console `
  -f net11.0 -r win-x64 --self-contained true `
  -p:PublishAot=true -p:PublishTrimmed=true
```

## Testing

`dotnet test` runs the unit, generator, and integration suites. The integration suite launches the
official **Testy** agent, which needs Rust on first run (or set `ACP_TESTY_BIN`).

```powershell
git clone https://github.com/agentclientprotocol/rust-sdk.git rust-sdk
git -C rust-sdk checkout f5c0eef0972bbda8e75a151aeb088642911b3568
cargo build --manifest-path rust-sdk/Cargo.toml -p agent-client-protocol-test --bins
$env:ACP_TESTY_BIN = (Resolve-Path rust-sdk/target/debug/testy.exe)
$env:ACP_MCP_ECHO_BIN = (Resolve-Path rust-sdk/target/debug/mcp-echo-server.exe)
dotnet test tests/SvcSystems.AI.AgentClientProtocol.IntegrationTests --framework net11.0
```

### ACP TCK

[ACP TCK](https://github.com/agentclientprotocol/acp-tck) tests agents, not clients. The runner
drives its complete v1 suite against official reference agents and against this library's own
agent host via the sample agent, plus a .NET client interoperability suite against the same
fixtures. It does not claim TCK certification of this client.

```powershell
pwsh -File .\tools\Test-AcpTck.ps1
```

Requires Git, [uv](https://docs.astral.sh/uv/), and the .NET SDK. The first run checks out a pinned
TCK revision and provisions Python 3.14. All 56 registered v1 requirements must pass; reports land
under `artifacts/acp-tck`.

ACP v2 is not supported.

## License

MIT. See [LICENSE](LICENSE).
