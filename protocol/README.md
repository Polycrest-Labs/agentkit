# The wire protocol fixtures

Canonical frame fixtures for the AgentKit v2 wire protocol — the single source both halves
conformance-test against. The C# suite (`tests/AgentKit.Tests/WireFixtureTests.cs`) round-trips
them through `AgentWireJson`; the TS suite (`ui/src/lib/wire-fixtures.spec.ts`) parses them through
`parseFrame`. A frame added on one side without its fixture — or a fixture the other side cannot
handle — fails that repo's CI. The fixtures are also the stub transport's script vocabulary.

Each fixture file:

```jsonc
{
  "event": "token",            // the SSE event: name (the SOLE type discriminant)
  "payload": { "delta": "x" }, // the data: JSON — used when the payload is well-formed JSON
  "rawData": "{ not json",     // exact raw data: text — used for malformed/edge payloads
  "valid": true,               // must parse on the TS side / deserialize on the C# side
  "kind": "core",              // "core" (typed frame) or "domain" (host-defined payload)
  "roundTrip": true            // C# deserialize→serialize reproduces the payload (JSON-equal)
}
```

Invariants under test: payloads are non-null, non-array JSON objects; the event name is the sole
discriminant (`{...payload, type: eventName}` — a hostile payload `type` can never override it);
domain event names match `^[a-z][a-z0-9-]*$` and never shadow a core name; unknown payload fields
are tolerated; malformed payloads are rejected, never guessed at.
