import * as signalR from "@microsoft/signalr";
import { applyPatch } from "./patch.js";
import {
  Envelope,
  ErrorBody,
  PatchOp,
  PROTOCOL_VERSION,
  RamphastosError,
  SessionRole,
  StreamDirection,
} from "./protocol.js";
import { decodeFrame, RamphastosStream, toBase64, toBytes } from "./streams.js";

export interface ConnectOptions {
  /** Bearer token / session ticket for the hello handshake. */
  auth?: string;
  /** Requested role; defaults to "controller". */
  role?: SessionRole;
  /** Reattach to an existing session id. */
  sessionId?: string;
  /** Resume point for seq-log replay. */
  lastSeq?: number;
  /** App-defined connection identity — a window/tab id, device name, …
   *  Surfaces server-side as SessionConnection.Meta. */
  meta?: Record<string, unknown>;
  /** Fetch/websocket transport overrides for exotic hosts. */
  httpClient?: signalR.HttpClient;
  /**
   * Connect over a bare WebSocket with no negotiate request. Required when a
   * proxy forwards WebSocket frames only and cannot carry the negotiate POST.
   */
  webSocketOnly?: boolean;
}

type PendingResult = {
  resolve: (value: unknown) => void;
  reject: (err: RamphastosError) => void;
};

/** Lifecycle of the underlying connection+handshake. */
export type SessionState = "connecting" | "ready" | "reconnecting" | "closed";

type VmMeta = { type: string; commands: string[] };

/**
 * One Ramphastos session: SignalR connection, mirrored VM graph,
 * seq/ack bookkeeping, pending invoke correlation.
 */
export class RamphastosSession {
  private constructor(private readonly conn: signalR.HubConnection) {}

  sessionId?: string;
  role?: SessionRole;
  /** Bumped on every applied patch — cheap subscription signal for bindings. */
  version = 0;
  private _state: SessionState = "connecting";
  get state(): SessionState {
    return this._state;
  }

  private store: Record<string, unknown> = {};
  private meta: VmMeta = { type: "", commands: [] };

  /**
   * The mirrored root's wire type — `"client"` for client-authoritative
   * (mirrored) sessions, the app's VM type for server-authoritative ones.
   * Lets consumers tell the two topologies apart (e.g. whether named
   * streams are announced eagerly or must be pulled).
   */
  get vmType(): string {
    return this.meta.type;
  }
  private pending = new Map<string, PendingResult>();
  private pendingStreams = new Map<string, PendingResult>();
  /** Open streams by wire stream id. */
  readonly streams = new Map<number, RamphastosStream>();
  private streamListeners = new Set<(s: RamphastosStream) => void>();
  private stateListeners = new Set<(s: SessionState) => void>();
  private errorListeners = new Set<(e: RamphastosError) => void>();
  private changeListeners = new Set<() => void>();
  private eventListeners = new Map<string, Set<(payload: unknown) => void>>();
  private invokeCounter = 0;
  private lastSeq?: number;
  private opts: ConnectOptions = {};

  static async connect(url: string, opts: ConnectOptions = {}): Promise<RamphastosSession> {
    const conn = new signalR.HubConnectionBuilder()
      .withUrl(url, {
        httpClient: opts.httpClient,
        ...(opts.webSocketOnly
          ? { transport: signalR.HttpTransportType.WebSockets, skipNegotiation: true }
          : {}),
      })
      .withAutomaticReconnect()
      .build();

    const session = new RamphastosSession(conn);
    session.opts = opts;
    conn.on("message", (m: Envelope) => session.dispatch(m));
    conn.on("frame", (raw: unknown) => session.dispatchFrame(toBytes(raw)));
    // A reconnected socket is a NEW connection id — reattach to the session
    // with a hello carrying our seq position so missed patches replay.
    conn.onreconnecting(() => session.setState("reconnecting"));
    conn.onreconnected(() => session.rehello());
    conn.onclose((err) => session.transportClosed(err));

    session.armReady();
    await conn.start();
    await session.hello(opts.sessionId, opts.lastSeq);
    await session.ready;
    return session;
  }

  private setState(state: SessionState): void {
    if (this._state === state) return;
    this._state = state;
    this.stateListeners.forEach((cb) => cb(state));
  }

  private emitError(err: RamphastosError): void {
    this.errorListeners.forEach((cb) => cb(err));
  }

  private intentionalClose = false;

  private transportClosed(err?: Error): void {
    // Pending work died with the socket either way.
    const failure = new RamphastosError({
      code: "connection_closed",
      message: err?.message ?? "transport closed",
      fatal: true,
    });
    for (const [id, p] of this.pending) {
      this.pending.delete(id);
      p.reject(failure);
    }
    for (const [name, p] of this.pendingStreams) {
      this.pendingStreams.delete(name);
      p.reject(failure);
    }
    if (this.intentionalClose) return; // reconnect() will drive the state
    this.setState("closed");
    this.failReady(failure);
  }

  private ready: Promise<void> = Promise.resolve();
  private markReady: () => void = () => {};
  private failReady: (e: unknown) => void = () => {};

  private armReady(): void {
    this.ready = new Promise<void>((resolve, reject) => {
      this.markReady = () => {
        this.setState("ready");
        resolve();
      };
      this.failReady = reject;
    });
  }

  private async hello(sessionId?: string, lastSeq?: number): Promise<void> {
    const hello: Envelope = {
      v: PROTOCOL_VERSION,
      type: "hello",
      auth: this.opts.auth,
      session: sessionId,
      lastSeq,
      role: this.opts.role ?? "controller",
      meta: this.opts.meta,
    };
    stripUndefined(hello);
    await this.conn.send("Send", hello);
  }

  /**
   * Re-hello after an auto-reconnect. A denied hello (fatal error while
   * still "reconnecting") is retried with backoff: the denial may be a
   * transient race — e.g. after a server restart a viewer can reconnect
   * before the authority re-claims the session. Without the retry the
   * transport stays up but the session is silently dead.
   */
  private helloRetries = 0;

  private rehello(): void {
    void this.hello(this.sessionId, this.lastSeq);
  }

  private scheduleHelloRetry(): void {
    // 200ms, 400, 800, … capped at 5s, ~8 attempts ≈ 50s of tolerance.
    if (this.helloRetries >= 8) return;
    const delay = Math.min(200 * 2 ** this.helloRetries++, 5000);
    setTimeout(() => {
      if (this._state === "reconnecting") this.rehello();
    }, delay);
  }

  private dispatch(m: Envelope): void {
    if (typeof m.seq === "number") this.lastSeq = m.seq;

    switch (m.type) {
      case "welcome":
        this.helloRetries = 0;
        this.sessionId = m.session;
        this.role = (m as { role?: SessionRole }).role;
        // replay:true means the server streams missed patches instead of a
        // snapshot — the mirrored graph we already have is the resume base.
        if ((m as { replay?: boolean }).replay === true) this.markReady();
        break;
      case "snapshot": {
        const root = m["root"] as { type: string; props: Record<string, unknown>; commands: string[] };
        this.store = root.props;
        this.meta = { type: root.type, commands: root.commands ?? [] };
        this.bump();
        this.markReady();
        break;
      }
      case "patch": {
        // Immutable application: the new root keeps untouched branches
        // reference-stable so React memoization works on the mirror.
        this.store = applyPatch(this.store, (m["ops"] as PatchOp[]) ?? []);
        this.bump();
        break;
      }
      case "result": {
        const ref = m["ref"] as string;
        const p = this.pending.get(ref);
        if (!p) break;
        this.pending.delete(ref);
        const err = m["error"] as ErrorBody | undefined;
        if (err) p.reject(new RamphastosError(err));
        else p.resolve(m["value"]);
        break;
      }
      case "stream-opened": {
        // our stream-open was granted
        const name = m["stream"] as string;
        const stream = this.makeStream(
          name,
          m["streamId"] as number,
          (m["direction"] as StreamDirection) ?? "down",
        );
        const p = this.pendingStreams.get(name);
        if (p) {
          this.pendingStreams.delete(name);
          p.resolve(stream);
        }
        this.streamListeners.forEach((cb) => cb(stream));
        break;
      }
      case "stream-open": {
        // announcement for a stream someone else opened (or re-attach).
        // != null covers explicit nulls from serializers that emit them.
        const streamId = m["streamId"] as number | undefined;
        if (streamId == null || this.streams.has(streamId)) break;
        const stream = this.makeStream(
          m["stream"] as string,
          streamId,
          (m["direction"] as StreamDirection) ?? "down",
        );
        this.streamListeners.forEach((cb) => cb(stream));
        break;
      }
      case "stream-close": {
        const streamId = m["streamId"] as number | undefined;
        const s = streamId != null ? this.streams.get(streamId) : undefined;
        if (s) {
          this.streams.delete(s.streamId);
          s._closed(m["reason"] as string | undefined);
          this.streamListeners.forEach((cb) => cb(s));
        }
        break;
      }
      case "call": {
        const method = m["method"] as string;
        this.eventListeners.get(method)?.forEach((cb) => cb(m["args"]));
        break;
      }
      case "error": {
        const err = m["error"] as ErrorBody;
        if (err?.stream && this.pendingStreams.has(err.stream)) {
          const p = this.pendingStreams.get(err.stream)!;
          this.pendingStreams.delete(err.stream);
          p.reject(new RamphastosError(err));
        } else {
          this.emitError(new RamphastosError(err));
          if (err?.fatal) {
            this.failReady(new RamphastosError(err));
            if (this._state === "reconnecting") this.scheduleHelloRetry();
          }
        }
        break;
      }
    }
  }

  private dispatchFrame(frame: Uint8Array): void {
    const decoded = decodeFrame(frame);
    if (!decoded) return;
    this.streams.get(decoded.streamId)?._deliver(decoded.payload, decoded.seq);
  }

  private makeStream(name: string, streamId: number, direction: StreamDirection): RamphastosStream {
    const stream = new RamphastosStream(
      name,
      streamId,
      direction,
      (frame) => void this.conn.send("SendFrame", toBase64(frame)).catch(() => {}),
      (reason) =>
        void this.conn
          .send("Send", {
            v: PROTOCOL_VERSION,
            type: "stream-close",
            session: this.sessionId,
            stream: name,
            streamId,
            reason,
          })
          .catch(() => {}),
    );
    this.streams.set(streamId, stream);
    return stream;
  }

  /**
   * Open a named stream (controller role). Resolves with the stream once the
   * server acknowledges; rejects with RamphastosError on rejection.
   */
  openStream(
    name: string,
    opts: { direction?: StreamDirection; meta?: Record<string, unknown> } = {},
  ): Promise<RamphastosStream> {
    try {
      this.ensureReady();
    } catch (e) {
      return Promise.reject(e);
    }
    const msg: Envelope = {
      v: PROTOCOL_VERSION,
      type: "stream-open",
      session: this.sessionId,
      stream: name,
      direction: opts.direction ?? "down",
      meta: opts.meta,
    };
    stripUndefined(msg);
    const result = new Promise<RamphastosStream>((resolve, reject) =>
      this.pendingStreams.set(name, { resolve: resolve as (v: unknown) => void, reject }),
    );
    this.conn.send("Send", msg).catch((e) => {
      const p = this.pendingStreams.get(name);
      if (!p) return;
      this.pendingStreams.delete(name);
      p.reject(new RamphastosError({ code: "send_failed", message: e instanceof Error ? e.message : String(e) }));
    });
    return result;
  }

  /** Subscribe to stream announcements (server- or peer-initiated). */
  onStream(cb: (s: RamphastosStream) => void): () => void {
    this.streamListeners.add(cb);
    return () => this.streamListeners.delete(cb);
  }

  private bump(): void {
    this.version++;
    this.changeListeners.forEach((cb) => cb());
  }

  /** Subscribe to any change in the graph. Returns unsubscribe. */
  onChange(cb: () => void): () => void {
    this.changeListeners.add(cb);
    return () => this.changeListeners.delete(cb);
  }

  /** Subscribe to server→client "call" messages by method name. */
  onCall(method: string, cb: (args: unknown) => void): () => void {
    let set = this.eventListeners.get(method);
    if (!set) this.eventListeners.set(method, (set = new Set()));
    set.add(cb);
    return () => set.delete(cb);
  }

  /** Subscribe to connection state changes. Returns unsubscribe. */
  onStateChange(cb: (s: SessionState) => void): () => void {
    this.stateListeners.add(cb);
    return () => this.stateListeners.delete(cb);
  }

  /** Subscribe to non-fatal server errors (scoped stream errors excluded —
   *  those reject their pending openStream). Returns unsubscribe. */
  onError(cb: (e: RamphastosError) => void): () => void {
    this.errorListeners.add(cb);
    return () => this.errorListeners.delete(cb);
  }

  private ensureReady(): void {
    if (this._state !== "ready")
      throw new RamphastosError({
        code: "not_connected",
        message: `session is ${this._state}`,
      });
  }

  /** Invoke a command path (/vm/<name>) or a write (/props/<name>). */
  invoke(path: string, args: unknown[] = []): Promise<unknown> {
    try {
      this.ensureReady();
    } catch (e) {
      return Promise.reject(e);
    }
    const id = `c-${++this.invokeCounter}`;
    const msg: Envelope = {
      v: PROTOCOL_VERSION,
      type: "invoke",
      session: this.sessionId,
      ack: this.lastSeq,
      id,
      path,
      args,
    };
    stripUndefined(msg);
    const result = new Promise<unknown>((resolve, reject) =>
      this.pending.set(id, { resolve, reject })
    );
    this.conn.send("Send", msg).catch((e) => {
      const p = this.pending.get(id);
      if (!p) return;
      this.pending.delete(id);
      p.reject(new RamphastosError({ code: "send_failed", message: e instanceof Error ? e.message : String(e) }));
    });
    return result;
  }

  /**
   * Live view-model proxy: reads hit the mirrored graph, command names
   * become invoke functions, property assignment sends a write intent
   * (server-authoritative — the value lands via the returning patch).
   */
  vm<T = Record<string, unknown>>(): T {
    const session = this;
    return new Proxy(
      {},
      {
        get(_, prop: string | symbol) {
          if (typeof prop !== "string") return undefined;
          if (session.meta.commands.includes(prop))
            return (...args: unknown[]) => session.invoke(`/vm/${prop}`, args);
          return session.store[prop];
        },
        set(_, prop: string | symbol, value: unknown) {
          if (typeof prop === "string") void session.invoke(`/props/${prop}`, [value]);
          return true;
        },
        ownKeys() {
          return Reflect.ownKeys(session.store);
        },
        getOwnPropertyDescriptor() {
          return { enumerable: true, configurable: true };
        },
      }
    ) as T;
  }

  /**
   * Drop the socket and reattach to the same session with replay.
   * The mirrored graph is the resume base — the server streams only
   * the patches we missed while away.
   */
  async reconnect(): Promise<void> {
    this.armReady();
    this.setState("reconnecting");
    this.intentionalClose = true;
    try {
      await this.conn.stop();
      await this.conn.start();
      await this.hello(this.sessionId, this.lastSeq);
      await this.ready;
    } finally {
      this.intentionalClose = false;
    }
  }

  async disconnect(): Promise<void> {
    this.setState("closed");
    await this.conn
      .send("Send", { v: PROTOCOL_VERSION, type: "bye", session: this.sessionId })
      .catch(() => {});
    await this.conn.stop();
  }
}

function stripUndefined(obj: Record<string, unknown>): void {
  for (const k of Object.keys(obj)) if (obj[k] === undefined) delete obj[k];
}
