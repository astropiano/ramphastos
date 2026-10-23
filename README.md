# Ramphastos

Realtime UI state sync for .NET: a server-side view-model graph is tracked,
diffed and streamed to TypeScript clients over SignalR/WebSocket, so a React
(or plain TS) frontend renders live state without hand-written API calls.

## Packages

.NET (namespaces `Ramphastos.*`):

| Project | Purpose |
| --- | --- |
| `Ramphastos.Core` | Object-graph tracking, diffing, session model |
| `Ramphastos.Protocol` | Wire protocol types (hello, patches, streams) |
| `Ramphastos.Server` | ASP.NET Core hosting / SignalR endpoint |
| `Ramphastos.TestSdk` | Test helpers for session round-trips |
| `Ramphastos.Core.Facts` | xunit test suite |

npm (`@ramphastos` scope):

| Package | Purpose |
| --- | --- |
| `@ramphastos/ramphastos-client` | Framework-agnostic protocol client |
| `@ramphastos/ramphastos-react` | React bindings (`RamphastosProvider`, hooks) |

## Example

`examples/hello-world` hosts a `CounterVm` on ASP.NET Core and renders it in
React; `scenarios/ramphastos-example` runs the same demo in Docker
(`docker compose` in that directory).

## Building

```
dotnet build RamphastosUi.slnx
npm install
npm run build --workspace @ramphastos/ramphastos-client --workspace @ramphastos/ramphastos-react
```

## Testing

```
dotnet test RamphastosUi.slnx
```

## License

MIT — see [LICENSE](LICENSE).
