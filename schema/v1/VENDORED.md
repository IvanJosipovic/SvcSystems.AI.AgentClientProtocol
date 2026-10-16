# ACP v1 schema

The ACP v1 schema is the wire-format source of truth. The initial revision for this implementation is pinned to:

- Repository: <https://github.com/agentclientprotocol/agent-client-protocol>
- Commit: `3c229ec5192db768e8659c8cdfd3cbb38d4855c2`
- Schema: `schema/v1/schema.json`

Generated protocol models must be reproducible from this schema revision. Updating the pin requires reviewing the schema diff, regenerating the internal wire models and serializer metadata, and updating schema coverage tests.

Local generator annotations in `schema.json`:

- `McpServer` adds the explicit `stdio` discriminator emitted on the wire and marks its referenced `allOf` definitions as the concrete union variants.
- `ContentBlock` opts into preserving unrecognized discriminator variants so clients can forward them across protocol version differences.
