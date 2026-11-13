# ramphastos-react

React bindings for [Ramphastos](https://github.com/astropiano/ramphastos)
sync sessions — `useViewModel` over the replicated object graph.

Part of the [Ramphastos](https://github.com/astropiano/ramphastos)
monorepo. API is alpha and will change.

```tsx
import { RamphastosSession } from "ramphastos-client";
import { RamphastosProvider, useViewModel } from "ramphastos-react";

const session = await RamphastosSession.connect("http://localhost:5000/ramphastos");

function App() {
  const vm = useViewModel<CounterVm>();
  return <button onClick={() => vm.increment()}>{vm.count}</button>;
}

createRoot(el).render(
  <RamphastosProvider session={session}><App /></RamphastosProvider>
);
```

Requires `ramphastos-client` and React 18 or 19.

License: MIT.
