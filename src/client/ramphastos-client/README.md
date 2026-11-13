# ramphastos-client

Framework-agnostic TypeScript client for the
[Ramphastos](https://github.com/astropiano/ramphastos) sync protocol —
replicated object graphs, commands, and binary streams over SignalR.

Part of the [Ramphastos](https://github.com/astropiano/ramphastos)
monorepo. API is alpha and will change.

```ts
import { RamphastosSession } from "ramphastos-client";

const session = await RamphastosSession.connect("http://localhost:5000/ramphastos");

// the replicated object graph — live, patched server-side
const root = session.root;

// invoke a server-side command
await session.invoke("/vm/increment", 1);
```

Client-authoritative apps (devices, desktop agents behind NAT) can publish
their own model upstream with `RamphastosAuthority` — the server holds a
replica and fans it out to viewers.

React bindings: [`ramphastos-react`](https://www.npmjs.com/package/ramphastos-react).

License: MIT.
