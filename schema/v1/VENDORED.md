# ACP v1 schema

The ACP v1 schema is the wire-format source of truth. The initial revision for this implementation is pinned to:

- Repository: <https://github.com/agentclientprotocol/agent-client-protocol>
- Commit: `3c229ec5192db768e8659c8cdfd3cbb38d4855c2`
- Schema: `schema/v1/schema.json`

Generated protocol models must be reproducible from this schema revision. Updating the pin requires reviewing the schema diff, regenerating the internal wire models and serializer metadata, and updating schema coverage tests.
