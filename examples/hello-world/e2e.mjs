import { RamphastosSession } from "../../src/client/ramphastos-client/dist/index.js";

const URL = "http://localhost:5055/ramphastos";
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
let failures = 0;
const check = (name, cond) => {
  console.log(`${cond ? "PASS" : "FAIL"}  ${name}`);
  if (!cond) failures++;
};

// --- controller session ---
const s = await RamphastosSession.connect(URL);
const vm = s.vm();
check("session id assigned", typeof s.sessionId === "string" && s.sessionId.startsWith("sess_"));
check("role is controller", s.role === "controller");
check("snapshot: title", vm.title === "Ramphastos vNext");
check("snapshot: count=0", vm.count === 0);
check("snapshot: ServerOnly absent", vm.internalState === undefined);

// --- command + patch round-trip ---
await vm.increment(5);
await sleep(300);
check("increment(5) → count=5", vm.count === 5);

await vm.decrement();
await sleep(300);
check("decrement → count=4", vm.count === 4);

const echo = await vm.echo("hello-ramphastos");
check("echo returns value", echo === "hello-ramphastos");

// --- collection ops ---
await vm.addItem("alpha");
await vm.addItem("beta");
await sleep(300);
check("addItem ×2 → items length 2", Array.isArray(vm.items) && vm.items.length === 2);
check("items content", vm.items[0] === "alpha" && vm.items[1] === "beta");
await vm.clearItems();
await sleep(300);
check("clearItems → empty", vm.items.length === 0);

// --- property write intent ---
vm.title = "Written From Client";
await sleep(300);
check("write intent → patched back", vm.title === "Written From Client");

// --- multi-viewer on same session ---
const viewer = await RamphastosSession.connect(URL, { role: "viewer", sessionId: s.sessionId });
const vmv = viewer.vm();
check("viewer joined same session", viewer.sessionId === s.sessionId);
check("viewer sees count=4", vmv.count === 4);

await vm.increment(1);
await sleep(300);
check("viewer receives controller's patch", vmv.count === 5);

try {
  await vmv.increment(1);
  check("viewer invoke rejected", false);
} catch (e) {
  check("viewer invoke rejected", String(e.message).includes("forbidden"));
}

// --- reconnect + replay (same session object, graph is resume base) ---
await vm.increment(2);           // lands at count=7
await sleep(300);
await s.reconnect();
check("resume same session", s.sessionId.startsWith("sess_"));
check("replayed state intact (count=7)", vm.count === 7);
await vm.increment(1);
await sleep(300);
check("invoke works after resume", vm.count === 8);

await s.disconnect();
await viewer.disconnect();

console.log(failures === 0 ? "\nALL PASSED" : `\n${failures} FAILURES`);
process.exit(failures === 0 ? 0 : 1);
