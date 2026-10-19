import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { RamphastosSession } from "@astropiano/ramphastos-client";
import { RamphastosProvider } from "@astropiano/ramphastos-react";
import { App } from "./App";

async function main() {
  const session = await RamphastosSession.connect("http://localhost:5000/ramphastos");

  createRoot(document.getElementById("root")!).render(
    <StrictMode>
      <RamphastosProvider session={session}>
        <App />
      </RamphastosProvider>
    </StrictMode>
  );
}

void main();
