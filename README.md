# SvcSystems.AI.AgentClientProtocol

[![NuGet](https://img.shields.io/nuget/v/SvcSystems.AI.AgentClientProtocol)](https://www.nuget.org/packages/SvcSystems.AI.AgentClientProtocol)

A small .NET client library for the [Agent Client Protocol (ACP)](https://agentclientprotocol.com/). It launches an ACP agent over stdio and gives applications a session-oriented API rather than exposing JSON-RPC or process plumbing.

Targets `net11.0` (currently using .NET 11 RC2). The library is trim-analyzed and NativeAOT compatible. Reflection-based `System.Text.Json` serialization is disabled in the project.

## Quick start

```csharp
await using var client = await Client.StartAsync(
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

## Host capabilities

ACP callbacks are enabled only when the host provides a handler. The client can host filesystem, terminal, permission, and elicitation operations. Do not configure filesystem or terminal handlers unless the application has an appropriate workspace/command security policy.

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

## Schema-generated wire models

The internal wire model generator is an **incremental Roslyn source generator**. It accepts JSON Schema `AdditionalFiles`, parses them with Microsoft's `Microsoft.OpenApi` JSON Schema reader, and emits C# models into the current compilation. It does not depend on an ACP schema version or a `schema/v1` path. The checked-in ACP v1 schema is simply the first input file used by this library.

The generator emits typed object models, tagged hierarchies, C# union types, and trim-safe JSON writers from the schema. Primitive aliases remain CLR primitives, and genuinely open JSON payloads such as `_meta` remain `JsonElement`-based. Protocol wire models are internal except for schema types that are part of the public API, such as `Implementation`.

The library uses `Utf8JsonWriter`, `Utf8JsonReader`, and `JsonDocument` for ACP framing and typed protocol mapping. It does not use reflection-based `JsonSerializer` overloads.

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

## Scope

The initial transport is stdio. The library targets stable ACP v1 and keeps the JSON-RPC implementation and generated wire types internal. HTTP/WebSocket transports and agent-specific APIs are not included.

## License

MIT. See [LICENSE](LICENSE).
