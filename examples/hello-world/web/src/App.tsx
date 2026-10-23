import { useState } from "react";
import { useViewModel } from "@ramphastos/ramphastos-react";
import type { CounterVm } from "./vm";

export function App() {
  const vm = useViewModel<CounterVm>();
  const [itemText, setItemText] = useState("");
  const [echoed, setEchoed] = useState("");

  return (
    <main style={{ fontFamily: "system-ui", maxWidth: 480, margin: "4rem auto" }}>
      <h1>{vm.title}</h1>

      <section>
        <h2>Counter: {vm.count}</h2>
        <button onClick={() => vm.decrement()}>−</button>{" "}
        <button onClick={() => vm.increment()}>+1</button>{" "}
        <button onClick={() => vm.increment(10)}>+10</button>
      </section>

      <section>
        <h2>Items</h2>
        <ul>
          {(vm.items ?? []).map((it, i) => (
            <li key={i}>{it}</li>
          ))}
        </ul>
        <input
          value={itemText}
          onChange={(e) => setItemText(e.target.value)}
          placeholder="new item"
        />{" "}
        <button
          onClick={() => {
            void vm.addItem(itemText);
            setItemText("");
          }}
        >
          add
        </button>{" "}
        <button onClick={() => vm.clearItems()}>clear</button>
      </section>

      <section>
        <button onClick={async () => setEchoed(String(await vm.echo("ping")))}>
          echo "ping"
        </button>{" "}
        {echoed && <span>→ {echoed}</span>}
      </section>
    </main>
  );
}
