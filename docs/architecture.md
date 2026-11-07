# Ramphastos UI — Architecture (vNext)

Status: draft for review
Date: 2026-09-26

## 1. Vision

Ramphastos UI vNext is a **server-side MVVM framework**: view-models live on the
server as C# objects; their state is replicated to clients as an observable
object graph; commands invoked on the client execute server-side. The first
client binding is React, but the protocol and client core are
**frontend-agnostic** — any UI layer that can consume JSON patches and invoke
RPCs can host a Ramphastos app.

The previous generation (RamphastosUi, 2013–2023) proved the model in
production but coupled it to AngularJS, a self-maintained CefGlue fork, and a
threading model that required deadlock watchdogs. vNext keeps the sync engine
concept and discards everything that made it expensive to maintain.

### Design goals

1. **Protocol is the product.** The versioned wire contract is stable;
   server and client implementations may evolve independently.
2. **One code path for local and remote.** A Tauri sidecar, a kiosk browser,
   and a remote SaaS session all speak the same protocol to the same server.
3. **No locks in user code.** All per-session concurrency is serialized through
   an actor mailbox; deadlocks are prevented by construction, not detection.
4. **Sessions are portable.** Serialized sessions survive reconnects, node
   migration, and — within schema tolerance — version upgrades.
5. **N viewers, one graph.** Multi-viewer sessions (co-browse, signage,
   monitoring walls) are a first-class primitive, not an add-on.

### Non-goals

- Serving views, templates, or static frontend assets (the frontend is an SPA
  owned by the app; the server is a pure sync endpoint).
- Mass-consumer scale (thousands of anonymous concurrent sessions per node).
- Offline-first clients. v1 is server-authoritative; optimistic local writes
  are a later option (§9.4).

## 2. System overview

```
┌───────────────────────────┐         ┌──────────────────────────────┐
│  Client (React SPA)       │         │  ASP.NET Core host           │
│                           │         │                              │
│  ramphastos-react (hooks) │         │  Ramphastos.Server           │
│         │                 │         │   - SignalR endpoint         │
│  ramphastos-client        │◄──WS───►│   - session manager          │
│  (graph store, patches)   │ SignalR │   - auth (JWT)               │
│                           │  JSON   │   - session providers        │
└───────────────────────────┘         │        │                     │
         hosted by:                   │  Ramphastos.Core             │
         - browser                    │   - VM graph + notifications │
         - Tauri webview (sidecar)    │   - session actor            │
         - kiosk Chromium             │   - commands, attributes     │
                                      └──────────────────────────────┘
                                                │
                                    session store: memory | file | Redis
```

## 3. Repository layout

```
ramphastos/
├── src/
│   ├── Ramphastos.Protocol/      # wire contracts only, zero deps
│   ├── Ramphastos.Core/          # VM graph, notification engine, session actor
│   ├── Ramphastos.Server/        # ASP.NET Core, SignalR, auth, providers
│   └── client/
│       ├── ramphastos-client/    # TS, framework-agnostic (npm package)
│       └── ramphastos-react/     # hooks over ramphastos-client (npm package)
├── examples/
│   └── hello-world/              # minimal app: VM + React UI + host
├── tauri/                        # reference Tauri shell (sidecar template)
└── docs/
```

One repo, server + client released independently: NuGet for the server,
npm for the client packages. Protocol version is the coupling point.

### Library dependencies

Two existing libraries become foundation dependencies:

- **`TotalSerializer.ReflectionTools`** — compiled/cached reflection accessors
  (`PropertyInfoCache`, `FieldInfoCache`, `MethodInfoCache`,
  `IMemberReader`/`IMemberWriter`). Used by `Ramphastos.Core` for all graph
  walking and member access (see §6.5). Replaces the old framework's private
  `Reflection/` helpers.
- **`TotalSerializer`** — full-fidelity object-graph serializer that
  captures private fields, **event registrations, and delegates**. Used for
  session persistence and inter-node migration (see §5.4). This is what makes
  *live* session moves possible rather than just data moves.

Both are source-available; consumed as NuGet packages.
Note: TotalSerializer is alpha — hardening it is part of this project's scope
(§12, M5).

## 4. Wire protocol

All messages are JSON. Every message carries a protocol version and, once
established, the session id and a sequence number.

### 4.1 Message envelope

```json
{
  "v": 1,
  "type": "hello | welcome | snapshot | patch | invoke | result | call | event | error | bye",
  "session": "sess_01J…",
  "seq": 128,
  "ack": 96
}
```

- `v` — protocol major version. Servers reject unknown majors.
- `seq` — monotonically increasing per direction, per session.
- `ack` — last seq received from the peer; enables replay-on-reconnect.

### 4.2 Handshake

```jsonc
// client → server
{ "v":1, "type":"hello",
  "auth": "eyJ…",                 // JWT or session ticket
  "session": "sess_01J…",         // optional: resume attempt
  "lastSeq": 128,                 // optional: resume point
  "role": "viewer | controller" } // optional: requested role

// server → client
{ "v":1, "type":"welcome",
  "session": "sess_01J…",
  "role": "viewer",               // granted role
  "seqBase": 100,
  "schema": "sha1:…",             // fingerprint of VM shape
  "replay": true }                // whether seq-based replay is possible
```

Resume contract: if the client's `lastSeq` is within the retained op log,
the server streams missed `patch` messages then `welcome`; otherwise it sends
a fresh `snapshot`. If `session` is unknown/expired, the server creates one.

### 4.3 Snapshot

Full serialization of the session's view-model graph. Sent on connect when
replay is impossible.

```jsonc
{ "v":1, "type":"snapshot",
  "root": { "$vm": "MyApp.MainVm", "props": { … }, "schema": "sha1:…" } }
```

Object identity uses reference ids for shared/cyclic graphs
(`{"$ref":"obj_17"}`), avoiding the old decycle module.

### 4.4 Patch (server → client)

State changes are emitted as **JSON Patch (RFC 6902)** op batches against the
replicated graph, plus one Ramphastos-specific op for collaborative text:

```jsonc
{ "v":1, "type":"patch", "ops":[
    {"op":"replace","path":"/counter/count","value":5},
    {"op":"add","path":"/items/-","value":{"name":"x"}},
    {"op":"remove","path":"/items/3"},
    {"op":"move","from":"/items/1","path":"/items/4"},
    {"op":"textpatch","path":"/doc/body","patch":"@@…diff_match_patch…@@"} ]}
```

- Paths are JSON Pointers into the VM graph.
- Ops are batched per actor turn (one turn = one patch message).
- `textpatch` preserves the old system's diff_match_patch string sync for
  high-frequency text editing.

### 4.5 Invoke / result (client → server)

```jsonc
// client → server
{ "v":1, "type":"invoke", "id":"c-41",
  "path":"/vm/save", "args":[ {"id":7} ] }

// server → client
{ "v":1, "type":"result", "ref":"c-41",
  "value":{ "ok":true } }        // or "error":{"code":"…","message":"…"}
```

Property writes from the client use the same envelope with
`type:"invoke", path:"/props/title", args:[newValue]` — i.e. writes are
*intents*, applied by the server inside the actor turn; the resulting state
flows back as a patch. There is no client-side write-through in v1.

### 4.6 Call (server → client)

Server-initiated client callbacks (successor to `JavaScriptViewModel`):

```jsonc
{ "v":1, "type":"call", "id":"s-9",
  "target":"view", "method":"scrollToTop", "args":[] }
```

Surfaces in client bindings as subscribed events / registered handlers.
Kept deliberately narrow — no return value in v1.

### 4.7 Errors and teardown

`error` messages carry `{code, message, fatal}`; `bye` initiates graceful
teardown. Fatal codes: `protocol_mismatch`, `auth_expired`,
`session_corrupted`, `schema_drift`. An optional `stream` field scopes an
error to a stream (e.g. a rejected `stream-open`).

### 4.8 Streams — the data plane

Named byte streams alongside the replicated graph: PTY output, file
transfer, live feeds. Control rides the envelope channel; payload bytes
never enter JSON.

```jsonc
// client → server (controller role)
{ "v":1, "type":"stream-open", "stream":"pty:main", "direction":"duplex",
  "meta":{ "cols":120, "rows":40 } }
// server → opener
{ "v":1, "type":"stream-opened", "stream":"pty:main", "streamId":7,
  "direction":"duplex" }
// server → every attached connection (announcement)
{ "v":1, "type":"stream-open", "stream":"pty:main", "streamId":7,
  "direction":"duplex" }
// either direction
{ "v":1, "type":"stream-close", "stream":"pty:main", "streamId":7,
  "reason":"exit 0" }
```

Payload frames are a fixed binary layout — identical on every transport:

```
[0]    u8   flags/version (currently 0)
[1..4] u32  streamId, little-endian
[5..8] u32  per-stream seq, little-endian
[9..]  payload
```

On SignalR, frames travel through a parallel hub method pair
(`SendFrame` / `"frame"` callback) as `byte[]` — base64 under the JSON
protocol, true binary under MessagePack or a raw-WebSocket transport; the
frame format never changes.

Semantics:

- The application **declares** stream endpoints (`session.DeclareStream`)
  with a direction — `down` (server→clients), `up` (client→server),
  `duplex`. A client `stream-open` resolves against the declaration; the
  granted direction is always the declared one (client may narrow, never
  widen).
- Upstream frames require the controller role and dispatch into the
  session actor's turn — handlers see them in order, serialized with
  commands.
- Downstream writes fan out to every attached connection, seq'd per
  stream. Connections attaching later learn open streams via the
  `stream-open` announcement after their snapshot/replay.
- Streams are **live resources**: they close with the session and are not
  persisted or replayed by the op-log. Scrollback/attach-replay of
  terminal data is application state, not transport state.
- Malformed frames are dropped; unknown ids are ignored; direction and
  role violations return a scoped `error`.

### 4.9 Client-authoritative sessions — the mirror model

The default topology is server-authoritative: the `Session` actor owns the
VM and clients attach as viewers/controllers. A second topology exists for
client apps that own their own model — the axn-term desktop is the canonical
example: its PTYs and workspace state live on the machine, and it reaches a
relay server through an **outbound** connection only (no inbound ports).

One connection per session may attach with `hello.role = "authority"`. That
session becomes a `ClientSession`: the server holds a **replica**, not the
model.

- **State flows upstream**: the authority pushes `snapshot` (full state
  tree) and `patch` (ops relative to its state root). The server applies
  them to a `JsonObject` replica via the same patch semantics the client
  mirror uses (`JsonPatchApplier` — shared semantics, incl. `textpatch`),
  appends them to the op-log, and fans them out to attached viewers
  unchanged.
- **Clients see the same wire**: a viewer's snapshot is the replica wrapped
  as `{type:"client", props:<state>}` — the client library cannot tell a
  mirrored session from a server-owned one. Replay/resume (lastSeq) works
  identically against the op-log.
- **Commands tunnel**: a controller's `invoke` is forwarded to the
  authority connection (same envelope); the authority answers with
  `result{ref}` which routes back to the requester. Pending invokes fail
  fast with `authority_offline` when the client disconnects.
- **Streams tunnel**: a client `stream-open` forwards to the authority,
  which grants by answering `stream-open` (the server assigns the id, sends
  `stream-opened` to requester and authority, announces to peers) or denies
  with a stream-scoped `error`. Data frames relay verbatim in both
  directions — upstream controller frames go only to the authority;
  authority frames fan out to all clients per the negotiated direction.
  Authority-initiated streams (`authority → stream-open`) work the same way.
- **Lifecycle**: the replica survives the authority — viewers keep last
  known state and late joiners still get a snapshot while the client is
  offline. Streams are live resources owned by the client: they die on
  authority disconnect. The mirror persists via `ISessionProvider` like any
  session (record `vmType:"client"`, state = replica) and resumes for
  viewers even before the authority reattaches.
- **Guards**: viewers/controllers pushing `snapshot`/`patch`/`result` get a
  `forbidden` error; an authority `hello` against a server-owned session id
  is a fatal rejection; a bad upstream patch op is dropped (the good ops in
  the same message still apply) and reported back to the authority.

The client side ships as `RamphastosAuthority` in ramphastos-client:
`publishSnapshot`/`publishPatch`, `onInvoke` (auto-result from the return
value), `onStreamRequest` (grant/deny), `openStream`, and the same
reconnect/state machinery as `RamphastosSession`. Both topologies share one
protocol — a hosted remote (e.g. axn-term's docker session-host) is just a
server-owned `Session`; the same `ramphastos-client` consumer code serves
both.

## 5. Session model

### 5.1 The session actor — core concurrency decision

Each session is an actor: a `Channel<SessionOp>` mailbox owned by
`SessionContainer`. **All** work touching the session's VM graph is posted to
the mailbox and executed serially:

- SignalR connection/disconnection events
- `invoke` messages (commands and property writes)
- property-change notifications raised inside VM code
- timers / app callbacks posting back (`Post()` API replaces the old
  `RamphastosDispatcher` + `SynchronizationContext` plumbing)

No locks exist on view-model objects. There are no session lock objects, no
throttling operator for correctness, and no deadlock health check — the
failure class those components managed is eliminated by construction.
Per-session throughput is bounded by one actor turn's cost, which is the same
trade-off Blazor Server's circuit dispatcher makes.

`Throttling` survives in a different role: optional back-pressure (drop /
coalesce / reject ops per session) to protect the server from abusive or
runaway sessions — a policy feature, not a correctness mechanism.

### 5.2 Lifecycle

```
connecting → active → (idle) → suspended → expired → disposed
                 │        ▲         │
                 └── reconnect ─────┘ (replay or snapshot)
```

- **active**: at least one live connection.
- **suspended**: no connections; VM graph held in memory (or serialized to
  provider) awaiting resume.
- **expired/disposed**: TTL passed; serialized record deleted.

### 5.3 Connections and roles

A session has N connections. Each connection has a **role**:

| Role | Receives patches | May invoke |
|---|---|---|
| `controller` | yes | yes |
| `viewer` | yes | no |
| `authority` | — (pushes state upstream) | owns commands; answers forwarded invokes |

`authority` exists only on client-authoritative sessions (§4.9): it writes
the replica rather than consuming it.

This makes co-browse/support and signage the same primitive: extra
connections are cheap because the heavy state (the VM graph) exists once per
session. Window→connection mapping from the old `SessionConnections` is
preserved for multi-window apps.

**Open point (§11):** the inverse subscription — one console connection
observing *many* sessions (signage fleet wall) — needs a read-only
cross-session subscriber that does not exist in the old model.

### 5.4 Persistence and live migration — TotalSerializer

`ISessionProvider` stores a session record:

```jsonc
{ "sessionId": "…", "created": "…", "lastSeq": 128,
  "vmType": "MyApp.MainVm", "schema": "sha1:…", "protocol": 1,
  "stateFormat": "stj|ts", "state": "<codec payload>", "meta": { "user": "…" } }
```

`state` is written through a pluggable `ISessionStateCodec` (`SessionOptions.Codec`):

- `"stj"` — `SessionStateCodecJson` (default): the member-model policy
  projection (`[ServerOnly]` included, `[Shared]` excluded), hydrated into a
  factory-created VM on resume.
- `"ts"` — `SessionStateCodecTotalSerializer`: the *whole* VM graph
  (private fields, delegates, cycles) as an opaque blob; resume deserializes
  the VM directly — wiring survives, live migration not rehydration. Reads
  legacy `"stj"` records as an upgrade path. Node-bound members are excluded
  via `Config.IgnoreType` / `RegisterRecreateTypeFactory<T>` through the
  codec's `configure` hook; `PropertyChangedEventHandler` /
  `NotifyCollectionChangedEventHandler` are ignored by default (tracker
  wiring is session machinery — re-subscribed on attach).

Providers: `InMemory` (dev), `Files` (single-node), `Redis` (multi-node —
any node loads a record another node wrote; native key TTL).

Schema gate on resume: fingerprint match → decode; mismatch →
`ISessionMigrator` chain (`SessionOptions.Migrators`) maps the old record
state, else fresh session. Unreadable `stateFormat` → fresh session; a
codec can never silently mis-decode a foreign record.

**Why TotalSerializer is the load-bearing choice:** a session is not just
data — it is a *live object graph with wiring*: VM-internal event
subscriptions, `CanExecute` handlers, timers and delegates registered by app
code. The old system could only move the data and then had to re-attach
wiring by hand on resume (a classic source of subtle resume bugs).
TotalSerializer serializes all fields including private ones *and* delegate
invocation lists, so a session can be serialized wholesale on node A,
deserialized on node B, and continue running with subscriptions intact —
**live migration, not rehydration**.

Node-bound members must not travel. They are excluded via TotalSerializer
configuration at record time:

- `[TotalSerializerIgnoreField]` / `Config.IgnoreType` — SignalR connection
  ids, hub references, the actor `Channel`, sockets.
- `Config.RegisterRecreateTypeFactory<T>` — things that must come from the
  *new* node's context instead of the serialized state: loggers,
  `CancellationTokenSource` (a reference implementation exists as
  `CancelationTokenSourceSerializer`), DI-bound services. On deserialize, the
  factory supplies a fresh node-local instance.
- `TotalSpecialSerializer` — for types needing a replacement instance
  (the library ships `WeakReferenceSerializer`, `TransparentProxySerializer`,
  `FieldInfoSerializer`, … as the pattern to follow).

Consequence for `ApplicationModel` design: keep node-bound state out of
the VM graph (services via `Post()`/factories, not fields), so sessions stay
freely serializable. This rule is enforced by a serialization check in the
test SDK (a session must round-trip cleanly through TotalSerializer).

On reconnect to a different node, the node loads the record, deserializes the
session, and resumes — the migrate-don't-pin model replaces strict sticky
sessions (SignalR still needs a shared backplane *or* URL-based node affinity
for the socket itself).

**Version tolerance — honest constraint:** TotalSerializer explicitly does
*not* support schema evolution; renamed/changed fields break deserialization.
So the record's `schema` fingerprint is the gate: **same fingerprint → direct
deserialize (live resume); mismatch → `ISessionMigrator` path**, which either
maps the old state onto a newly constructed VM via declared migrators or
degrades to a fresh session. Never silently deserialize into a drifted
schema — that produces corrupt wiring, the worst failure mode for this
library specifically.

**Security note:** TotalSerializer instantiates arbitrary types on read —
the session store is trusted content only. Client input never becomes a
session payload.

## 6. Server-side programming model

### 6.1 The synced object model

The type hierarchy mirrors the old
`ApplicationModel → WindowModel → view-model` split — same roles, clearer
names:

```csharp
SyncedObject          // any node in the synced graph: Set/INPC + [Command]
├── ApplicationModel  // the session root — whole-app state + connection hooks
├── WindowModel       // a window: Id/Title/Icon/State/HasFocus/Content/…
└── (app types)       // content view-models, models — plain synced nodes
```

- **`SyncedObject`** is the unit of synchronization (the old
  `RamphastosObject`). Deep-graph change tracking applies at any depth:
  nested objects and collections diff into JSON Pointers automatically.
  Commands are invocable on any node, not just the root.
- **`ApplicationModel`** is the root of a session's graph — what the
  `VmFactory` produces. It adds the connection lifecycle hooks
  (`OnClientConnected`/`OnClientDisconnected`, §6.6). Not called "view-model"
  because it *is* the application — most of its members aren't bound to any
  single view.
- **`WindowModel`** is a window: `Id`, `Title`, `Icon`,
  `State` (normal/minimized/maximized/fullscreen), `HasFocus`,
  `OwnerConnection`, and `Content` — the synced object hosted inside it.
  How a host realizes it (browser tab, in-page window, Tauri/CEF/OS window)
  is the host's business; the model only carries the window's state.
  Swapping `Content` is navigation — the window persists.
- **View-model is a role, not a type**: any synced object a view binds to —
  a `WindowModel.Content`, a session root's member — plays it. Pure domain
  state (cells, documents) is just `SyncedObject`.

```csharp
public class CounterVm : ApplicationModel       // session root
{
    public int Count { get; set; }              // tracked: set → patch
    public ObservableCollection<Item> Items { get; } // tracked collection

    [Command]                                   // exposed to clients
    public Task Increment(int by = 1) { Count += by; return Task.CompletedTask; }

    [ServerOnly]                                // never leaves the server
    public string DbConnectionId { get; set; }
}
```

- Authorization on commands: `[Command(RequireRole="controller")]`,
  policy-checked inside the actor turn — enforced server-side, client role is
  display-only convenience.
- Command objects (`RamphastosCommand` with `CanExecute`) remain available for
  the classic pattern; `[Command]` methods are the ergonomic default.

### 6.2 Attributes (serialization policy)

| Attribute | Effect |
|---|---|
| `[ServerOnly]` | property exists server-side only (old `SessionOnly`) |
| `[NoSync]` | not serialized to clients (old `NoSerialize`) |
| `[SyncReduced]` | coalesced/lossy sync for high-frequency values |
| `[TextSync]` | opt-in diff_match_patch string sync |
| `[Shared]` | resolves to a SharedObject instance across sessions |

Default policy remains opt-out (compat-friendly) but the serializer takes a
pluggable `ISyncPolicy` so apps can flip to opt-in (`[Sync]`-only) for
sensitive surfaces.

### 6.3 SharedObject

Fleet case: one VM instance shared across sessions/viewers (all signage
screens showing identical content). Implemented:

- `SessionOptions.SharedObjects` maps registry keys → factories; the
  manager owns a `SharedObjectRegistry` that materializes instances lazily.
- A session member marked `[Shared]` (optional `Name`; defaults to the
  member type's full name) is resolved at session creation: the registry
  instance is assigned and the session subscribes to that shared object's
  patch stream.
- Each `SharedObject` is its own actor (mailbox + `GraphTracker` at path
  `""`); mutations go through `manager.SharedObjects.Post<T>(...)`. Emitted
  ops fan out to subscribers rebased to the member path (`/fleet/...`) and
  splice into each session's pending ops — one seq stream, replay/snapshot
  semantics unchanged.
- Session trackers deliberately skip `[Shared]` subtrees — the shared
  object is the single owner of its change notification, which avoids
  double-emit and foreign-thread tracker mutation.
- Shared state is **live**: wire-visible in snapshots and patches but
  excluded from `SessionRecord` and hydration (it is re-resolved from the
  registry on resume). Write intents on shared members are rejected as
  `forbidden`; shared content is server-driven.

### 6.4 Post / timers / async

```csharp
Post(() => { /* runs inside the session actor turn */ });
```

replaces the dispatcher. Long-running async work runs *outside* the actor and
posts results back — the actor turn is never blocked awaiting I/O.

### 6.5 Reflection layer — TotalSerializer.ReflectionTools

The diff engine walks the VM graph on every actor turn; member access is a
hot path. All reflection goes through `TotalSerializer.ReflectionTools`:

- `PropertyInfoCache` / `FieldInfoCache` / `MethodInfoCache` — replaces the
  old private caches, deleted.
- `IMemberReader` / `IMemberWriter` compiled accessors — used by the
  differ and by the attribute-policy evaluator.
- Schema fingerprints (§5.4) are computed by walking the ReflectionTools
  member model and hashing serializable member names/types — so the
  fingerprint and the serializer always agree.

### 6.6 Connections and windows

A browser tab, signage screen, or desktop window is a **connection** — the
framework supplies identity and lifecycle primitives; the app models the
windows it wants on top:

- `HelloMessage.Meta` → `SessionConnection.Meta`: the client announces
  app-defined identity at connect (`connect(url, { meta: { window: "w2" } })`) —
  a window key, device name, or anything else. Carried by viewer, controller,
  and authority hellos alike.
- `ApplicationModel.OnClientConnected(connection)` / `OnClientDisconnected(connection)`
  run inside the session actor. The connect hook fires *after* the joiner's
  snapshot/replay, so hook mutations reach the joiner as ordinary patch ops
  (no double-apply). The typical hook body adds/removes the app's
  `WindowModel` for `connection.Meta["window"]` with
  `OwnerConnection = connection.Id`.
- `ConnectionContext.Current` is an `AsyncLocal` set around every command and
  property write — the answer to "which window invoked this".

An app-level `Windows` collection of `WindowModel` gives the shared desktop:
every connection sees the same open windows. Window lifecycle is app
policy — the close command looks the window up, calls `OnClosing()`
(veto, e.g. unsaved content), removes it, then `OnClosed()`. A tab dying
abruptly is `OnClientDisconnected` for the window's `OwnerConnection`.
Focus (`HasFocus` + an app "last focused" member) and navigation
(`window.Content = …`) are ordinary synced state.

What is deliberately not built: a framework `Windows` registry or window
manager (the old codebase had one) — whether every connection gets a
window at all is app policy (viewers usually aren't windows). Per-connection
*synced* state slices and presence/cursor broadcast are follow-ups if a real
case appears; per-tab local state can stay client-side.

## 7. Client architecture

### 7.1 ramphastos-client (framework-agnostic)

```ts
const session = await ramphastos.connect(url, {
  auth: token,
  resume: true,                    // send lastSeq when available
});

const vm = session.vm<CounterVm>("main");   // live graph proxy
vm.count;                                    // reactive read
await vm.invoke("/vm/increment", 1);         // command
vm.on("call", (m) => …);                     // server→client calls
session.on("patch", …);                      // raw stream (debug/bridge)
```

- Maintains the mirrored object graph; applies patch ops; emits node-level
  change notifications so bindings re-render only affected subtrees.
- Reference-id resolution (`$ref`) → real object references on the client.
- No framework dependency. SignalR via `@microsoft/signalr`; transport is
  behind an interface so a raw-WebSocket transport can be added.

### 7.2 ramphastos-react

```tsx
function Counter() {
  const vm = useViewModel<CounterVm>("main");
  return <button onClick={() => vm.increment(1)}>{vm.count}</button>;
}
```

- `useViewModel(name)` — subscribes the component to the VM subtree;
  re-renders on node changes under subscribed paths.
- Commands materialize as async methods on the proxy (typed via a generated
  or hand-written `d.ts` contract).
- `useProperty(vm, "items.3.name")` — fine-grained subscription.
- `RamphastosProvider` — session lifecycle, suspense on first snapshot,
  error boundary on `fatal`.

### 7.3 Other frontends

The client contract (snapshot → patches → invoke) is deliberately small:
a Vue/Svelte binding or a non-JS consumer (MAUI, embedded) implements the
same store interface. React is first because it's the ecosystem default —
not because the protocol favors it.

## 8. Desktop and embedded hosting

### 8.1 Tauri (replaces CefGlue)

The old framework's local mode embedded Chromium via a private CefGlue fork —
the single largest maintenance sink. vNext has **no embedded browser code**.

- **Remote mode**: Tauri app loads the app URL; session is remote.
- **Sidecar mode**: Tauri bundles the ASP.NET server binary as a sidecar
  process; webview connects to `http://localhost:<port>`. Local and remote
  become the same code path — the old parallel `LocalViewClient` plumbing is
  deleted.
- Native features (tray, window mgmt, auto-update) come from Tauri, not from
  Ramphastos — Ramphastos stays hosting-agnostic.

Caveat: Linux uses WebKitGTK (not Chromium). The React client must be tested
there early; nothing in the protocol depends on engine.

### 8.2 Kiosk / signage

Stock Chromium/Firefox in kiosk mode on the device → server URL. Session
resume turns power-loss into a non-event: browser relaunches, `hello`
with `session` + `lastSeq`, replay or snapshot, screen is back. No native
code on the player at all beyond an OS browser.

## 9. Cross-cutting concerns

### 9.1 Security

- JWT auth at `hello`; re-auth on expiry via `error auth_expired` → re-`hello`.
- Roles enforced server-side on every `invoke` (viewers cannot execute).
- Opt-out serialization default + opt-in policy option (§6.2).
- Consider: signed session tickets for resume, so a stolen `sessionId` alone
  cannot reattach a session.

### 9.2 Serialization

Two serializers, two jobs — do not conflate them:

- **Wire protocol** (server ↔ client): System.Text.Json `JsonNode` at the
  protocol boundary (replacing the Newtonsoft `JToken` layer — same
  dynamic-graph capability). `textpatch` keeps diff_match_patch semantics.
  Reference ids replace JsonNetDecycle. This serializer honors the §6.2
  attribute policy (`ServerOnly`, `NoSync`, …) — it decides what *leaves the
  server*.
- **Session persistence** (node ↔ store): TotalSerializer — full-fidelity,
  captures *server* state including `[ServerOnly]` members and wiring. It
  decides what *moves between nodes*. The two policies are independent and
  must stay that way: `ServerOnly` state travels in session records but never
  in wire messages.

### 9.3 Scaling model

State per session is the deliberate tax (accepted: deployment shapes are
bounded — devices, signage fleets, internal tools). Mitigations carried
forward: serialized portable sessions, SharedObject for fleets, attribute
footprint control. New in vNext: the seq-log replay gives cheap short-term
resume without touching the session provider.

### 9.4 Deferred

- Optimistic client writes (v1 is intent-only)
- Cross-session observation console API (fleet wall)
- Return values for server→client `call`
- Non-SignalR transports

## 10. What is deleted vs. carried forward

| Carried forward (concept) | Deleted |
|---|---|
| VM graph diff/patch sync | AngularJS, gulp, jQuery, Q, oclazyload |
| Property-level attribute control | Embedded-resource templating / Files pipeline |
| Commands + CanExecute | CefGlue, Awesomium, all presentation forks |
| Session providers, resume (→ live migration via TotalSerializer) | Old SignalR 2.x client path |
| Multi-connection sessions | `LocalViewClient` parallel plumbing |
| ApplicationModel → WindowModel → content layering, restored on `SyncedObject`/`ApplicationModel`/`WindowModel` (M6: hello meta, connection hooks, invoke context) | Old server-side WindowModel *manager* — apps own their `Windows` collection |
| diff_match_patch text sync | log4net, ConfigurationManager |
| Throttling (as policy) | `JavaScriptViewModel` dynamic proxies (→ `call`) |
| — | Private `Reflection/` caches (→ `TotalSerializer.ReflectionTools`) |
| — | Newtonsoft `JToken` plumbing (→ STJ `JsonNode` + TotalSerializer) |

## 11. Open questions for review

1. **Command surface**: `[Command]` methods (proposed) vs. command objects
   only vs. both. Both adds API surface; methods-only loses CanExecute.
2. **Opt-in vs opt-out serialization** as the *default* for new apps.
3. **Session ticket format** for resume security.
4. **Cross-session subscriber** — design now or defer to first fleet use case?
5. **Client store implementation** — mutable mirror + node events (proposed)
   vs. immer/zustand-based. Affects patch application cost.
6. **Naming** — keep `Ramphastos*` namespaces (decided)
7. **TotalSerializer maturity** — it is alpha; its deserialization path is
   ~70% slower than serialization. Acceptable for session suspend/resume
   (cold path), but the perf suite should gate M5, and we may need to
   contribute fixes upstream.
8. **Live-migration boundary** — should `Post()` queues and in-flight
   command state also serialize, or is "VM graph + wiring" the migration
   unit? Proposed: mailbox is drained before suspend; in-flight ops are not
   migrated.

## 12. Milestones

- **M0** — this document, reviewed
- **M1 — vertical spike**: `Ramphastos.Protocol` + minimal actor + SignalR
  echo of one VM to `ramphastos-react`; one command round-trip. Proves the
  loop end-to-end.
- **M2 — core**: full graph diff engine on `TotalSerializer.ReflectionTools`,
  collections, attributes, seq/replay, session lifecycle + providers
  (memory, file). Test SDK gains the TotalSerializer round-trip check (§5.4).
  *Status: done* — GraphTracker walks the whole object graph (nested INPC
  objects, `IDictionary`, `IEnumerable` at any depth), `PropertyReader`/
  `PropertyWriter` were added to ReflectionTools for compiled member access,
  `SyncReduced`/`TextSync`/`RequireRole` are honored, suspend→provider→
  resume with schema-fingerprint validation works (in-memory + file
  providers), and `Ramphastos.TestSdk` + `Ramphastos.Core.Facts` cover the
  loopback round-trip. The §5.4 TotalSerializer round-trip check exists in
  the TestSdk (`TotalSerializerRoundTrip`): VM graphs survive
  field-faithful serialization with change-notification subscriptions
  excluded via `Config.IgnoreType`. Wiring `SessionRecord.State` to a
  TS-native payload (instead of the STJ projection the providers store
  today) is still M5 scope.
- **M2a — data-plane streams**: `stream-open`/`stream-opened`/`stream-close`
  envelopes + framed binary payloads (`StreamFrame`), session stream
  registry with declarations, role/direction enforcement, SignalR frame
  channel, TS client `openStream`/stream objects. *Status: done.*
- **M3 — react client**: published `ramphastos-client` + `ramphastos-react`
  with hooks, roles, error handling. *Status: done (pack-ready, not yet in
  a registry)* — client gained `SessionState` (connecting/ready/
  reconnecting/closed), `onStateChange`/`onError`, invoke+openStream
  ready-guards, pending rejection on transport close, and reconnect-aware
  ready semantics; react gained `useSessionState`/`useRole`/`useCanInvoke`/
  `useRamphastosError`/`useStream` next to `useViewModel`. Both packages
  carry `exports`/`files`/`prepack` — `npm pack` produces clean tarballs;
  actual registry publish is deferred pending feed choice.
- **M4 — multi-viewer + shared objects**: done — roles and viewer
  connections were already covered by M2; `[Shared]`/`SharedObject`/
  `SharedObjectRegistry` now provide cross-session shared state (one actor
  per shared object, path-rebased op splicing, live-not-persisted).
- **M5 — Redis provider + live session migration**: done —
  `ISessionStateCodec` seam (`SessionStateCodecJson` default,
  `SessionStateCodecTotalSerializer` for whole-graph records with wiring
  intact), `ISessionMigrator` schema-drift chain, `SessionProviderRedis`
  (StackExchange.Redis, native TTL). ReflectionTools consumed as
  `0.1.0-alpha.6` (net6–net10 incl. compiled property accessors) and
  TotalSerializer rebuilt as `0.1.0-alpha.3` against it. Remaining M5
  hardening: TotalSerializer perf/edge validation under load.
- **M6 — connection/window model**: done — `HelloMessage.Meta` carries
  app-defined connection identity (a window/tab id, device name, …) onto
  `SessionConnection.Meta`; `ApplicationModel.OnClientConnected` /
  `OnClientDisconnected` fire inside the session actor (post-snapshot, so
  hook mutations reach the joiner as ordinary patch ops); and an ambient
  `ConnectionContext.Current` tells commands and property writes *which*
  connection invoked them. The type hierarchy mirrors the old
  ApplicationModel → WindowModel → view-model split: `SyncedObject` is any
  node in the graph (the old `RamphastosObject`), `ApplicationModel` is the
  session root, and `WindowModel` is a window — host-agnostic state the
  host realizes as a tab, in-page window, or OS window —
  `Id`/`Title`/`Icon`/`State`(normal|minimized|maximized|fullscreen)/
  `HasFocus`/`OwnerConnection`/`Content` (the hosted view, swappable =
  navigation while the window persists) plus `OnClosing`/`OnClosed`
  close-veto hooks. Apps keep a
  synced `Windows` collection maintained by the connection hooks — the old
  server-side WindowModel manager is superseded by these primitives.
- **Deferred — Tauri sidecar template + kiosk guide** (was M6): not a
  capability gap, it's packaging for a concrete app. §8 notes remain the
  guide; build the template when an app actually ships as desktop/kiosk.
  Client-authority (§4.9) does not need Tauri — `RamphastosAuthority`
  runs in any JS host; its e2e proof belongs with a real authority app.
