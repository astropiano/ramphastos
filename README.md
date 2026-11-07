# Ramphastos

<img src="branding/ramphastos.png" alt="Ramphastos" width="320" />

**Server-side MVVM for .NET — your view-models stay on the server, your UI stays a real SPA.**

Ramphastos keeps the view-model graph on the server as plain C# objects and
replicates it to clients as an observable object graph. Property changes flow
to clients as JSON Patch batches; commands invoked on the client execute
server-side inside a per-session actor. The first client binding is React,
but the wire protocol is frontend-agnostic.

> **Status: alpha.** Ramphastos is a ground-up revival of RamphastosUi
> (2013–2023), which ran the same model in production against AngularJS and a
> self-maintained CefGlue fork. The vNext rewrite keeps the sync-engine
> concept and rebuilds it on .NET 10, SignalR, and a versioned wire protocol.
> APIs will change.

## What you write

```csharp
public class CounterVm : ApplicationModel
{
    private int _count;
    public int Count { get => _count; set => Set(ref _count, value); }

    [Command]
    public Task Increment(int by = 1) { Count += by; return Task.CompletedTask; }
}
```

```tsx
const vm = useViewModel<CounterVm>();
return <button onClick={() => vm.increment()}>{vm.count}</button>;
```

## Features

- **Server-authoritative object graph** — the VM lives on the server; every
  client holds a live replica, patched per actor turn (RFC 6902 + a
  `textpatch` op for high-frequency text editing via diff-match-patch).
- **Commands, not endpoints** — client calls (`invoke`) and property writes
  are intents, serialized through the session actor. No locks in user code.
- **Multi-viewer sessions** — `viewer` and `controller` roles are
  first-class; co-browse, signage, and monitoring walls come free.
- **Portable sessions** — sessions persist (memory, file, Redis) and resume
  via seq/ack replay. Built on
  [TotalSerializer](https://github.com/astropiano/totalserializer), sessions
  capture private fields, event registrations, and delegates — so they can
  migrate between nodes *live*.
- **Client-authoritative mirror sessions** — a client app can own the model
  and reach a relay server through an outbound-only connection; the server
  holds a replica and fans it out to viewers.
- **Binary stream plane** — named byte streams (PTY output, file transfer,
  live feeds) multiplexed on the same session; control rides the JSON
  envelope, payload bytes never do.
- **Shared objects** — process-wide state mirrored into multiple sessions.

## Repository layout

```
src/
  Ramphastos.Protocol/        wire contracts only, zero dependencies
  Ramphastos.Core/            VM graph, notification engine, session actor
  Ramphastos.Server/          ASP.NET Core, SignalR, auth, providers
  Ramphastos.TestSdk/         in-memory test host + scripted test client
  Ramphastos.Core.Facts/      test suite (xunit)
  client/
    ramphastos-client/        @ramphastos/ramphastos-client (TS, framework-agnostic)
    ramphastos-react/         @ramphastos/ramphastos-react (hooks)
examples/hello-world/         minimal app: VM + React UI + host
docs/architecture.md          full architecture + wire protocol spec
branding/                     logo and icon assets
```

## Getting started

**Requirements:** .NET 10 SDK, Node.js 22+.

Server packages target `net10.0`; client packages are on npm under
`@ramphastos/*`.

```bash
dotnet add package Ramphastos.Server
npm add @ramphastos/ramphastos-client @ramphastos/ramphastos-react
```

```csharp
// Program.cs — minimal host
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRamphastos<CounterVm>();
var app = builder.Build();
app.MapRamphastos();              // SignalR endpoint at /ramphastos
app.Run();
```

Run the hello-world example (`examples/hello-world`): start
`HelloWorld.Server`, then `npm install && npm run dev` in `web/` and open the
Vite URL.

### Building from source

`Ramphastos.Core` depends on `TotalSerializer` and
`TotalSerializer.ReflectionTools`, which are not yet on nuget.org. Until they
are published, clone
[totalserializer](https://github.com/astropiano/totalserializer) next to this
repo — `nuget.config` resolves its local package output
(`../totalserializer/packages-local`) automatically.

```bash
git clone https://github.com/astropiano/ramphastos
git clone https://github.com/astropiano/totalserializer
cd totalserializer && dotnet pack -c Release -o packages-local
cd ../ramphastos
dotnet build Ramphastos.slnx
dotnet test  src/Ramphastos.Core.Facts
npm ci && npm run build --workspaces
```

## How it compares

Ramphastos sits between **Blazor Server** (which replicates a *render tree* —
Ramphastos replicates the view-model graph and leaves rendering to your SPA
framework), **Phoenix LiveView / Livewire** (which diff rendered HTML —
Ramphastos diffs data), and **ActualLab.Fusion** (which replicates computed
values for general reactive RPC — Ramphastos is a session-scoped UI framework
with roles, commands, streams, and portable sessions). The mirror topology —
a client-authoritative model replicated upstream through a relay — is,
as far as we know, unique.

## Documentation

- [docs/architecture.md](docs/architecture.md) — design goals, session
  model, the full wire protocol (envelope, handshake, patches, invoke/call,
  streams, mirror sessions), and the roadmap.
- `examples/hello-world` — end-to-end minimal app.

## Contributing

Early-stage and feedback-driven. Issues and design discussion are welcome;
please read `docs/architecture.md` first — the protocol is the product.

## License

[MIT](LICENSE). Ramphastos descends from RamphastosUi (2013–2023,
matchmycolor LLC), which was also released under MIT.
