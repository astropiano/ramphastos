import React, {
  createContext,
  useContext,
  useEffect,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from "react";
import type {
  RamphastosError,
  RamphastosSession,
  RamphastosStream,
  SessionRole,
  SessionState,
} from "@astropiano/ramphastos-client";

const SessionContext = createContext<RamphastosSession | null>(null);

export function RamphastosProvider({
  session,
  children,
}: {
  session: RamphastosSession;
  children: ReactNode;
}) {
  return <SessionContext.Provider value={session}>{children}</SessionContext.Provider>;
}

export function useRamphastosSession(): RamphastosSession {
  const session = useContext(SessionContext);
  if (!session) throw new Error("useRamphastosSession outside RamphastosProvider");
  return session;
}

/**
 * Subscribe to the session's mirrored view-model. Re-renders on every
 * applied patch; the returned proxy reads live values and exposes
 * commands as async functions.
 *
 *   const vm = useViewModel<CounterVm>();
 *   <button onClick={() => vm.increment(1)}>{vm.count}</button>
 */
export function useViewModel<T = Record<string, unknown>>(): T {
  const session = useRamphastosSession();
  useSyncExternalStore(
    (cb) => session.onChange(cb),
    () => session.version
  );
  return session.vm<T>();
}

/** Connection lifecycle: connecting | ready | reconnecting | closed. */
export function useSessionState(): SessionState {
  const session = useRamphastosSession();
  return useSyncExternalStore(
    (cb) => session.onStateChange(cb),
    () => session.state
  );
}

/** The negotiated role (set at welcome). */
export function useRole(): SessionRole | undefined {
  const session = useRamphastosSession();
  return useSyncExternalStore(
    (cb) => session.onStateChange(cb),
    () => session.role
  );
}

/** Whether this connection may invoke commands / write props / write streams. */
export function useCanInvoke(): boolean {
  return useRole() === "controller";
}

/** Subscribe to non-fatal server errors (invoke errors reject instead). */
export function useRamphastosError(cb: (e: RamphastosError) => void): void {
  const session = useRamphastosSession();
  useEffect(() => session.onError(cb), [session, cb]);
}

/** Subscribe to a server→client "call" by method name. */
export function useRamphastosCall(method: string, cb: (args: unknown) => void): void {
  const session = useRamphastosSession();
  useEffect(() => session.onCall(method, cb), [session, method, cb]);
}

/**
 * Observe a named stream — resolves when the stream exists (opened by us,
 * announced by the server or a peer). Undefined while absent.
 *
 *   const pty = useStream("pty:main");
 *   pty?.onData((bytes) => term.write(bytes));
 */
export function useStream(name: string): RamphastosStream | undefined {
  const session = useRamphastosSession();
  const find = () =>
    [...session.streams.values()].find((s) => s.name === name && !s.closed);
  const [stream, setStream] = useState<RamphastosStream | undefined>(find);
  useEffect(() => {
    setStream(find());
    return session.onStream((s) => {
      if (s.name === name) setStream(s.closed ? undefined : s);
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session, name]);
  return stream;
}
