import * as signalR from "@microsoft/signalr";
import {
  Envelope,
  ErrorBody,
  PatchOp,
  PROTOCOL_VERSION,
  RamphastosError,
  StreamDirection,
} from "./protocol.js";
import { decodeFrame, RamphastosStream, toBase64, toBytes } from "./streams.js";
import type { SessionState } from "./session.js";

export interface AuthorityConnectOptions {
  /** Bearer token / session ticket for the hello handshake. */
  auth?: string;
  /** Attach to an existing mirrored session (reconnect / resume). */
  sessionId?: string;
  /** Fetch/websocket transport overrides for exotic hosts. */
  httpClient?: signalR.HttpClient;
  /**
   * Connect over a bare WebSocket with no negotiate request. Required when a
   * proxy forwards WebSocket frames only and cannot carry the negotiate POST.
   */
  webSocketOnly?: boolean;
  /** App-defined connection identity — a window/device name, …
   *  Surfaces server-side as SessionConnection.Meta. */
  meta?: Record<string, unknown>;
}

/** A stream-open request forwarded from a client through the server. */
export interface StreamRequest {
  stream: string;
  direction: StreamDirection;
  meta?: Record<string, unknown>;
}

/** An invoke forwarded from a client through the server. */
export interface ForwardedInvoke {
  /** Correlation id — pass back to {@link RamphastosAuthority.respond}. */
  ref: string;
  path: string;
  args: unknown[];
}

type InvokeHandler = (invoke: ForwardedInvoke) => unknown | Promise<unknown>;
type StreamRequestHandler = (
  req: StreamRequest,
) => boolean | { direction?: StreamDirection } | Promise<boolean | { direction?: StreamDirection }>;

/**
 * The authority side of a client-authoritative session (§4.9): the client owns
 * the model, pushes snapshot/patch upstream, answers forwarded invokes and
 * stream requests, and tunnels stream frames through the server to clients.
 *
 * The wire facing viewers/controllers is identical to a server-owned
 * session — this class is the client-side counterpart to RamphastosSession.
 */
export class RamphastosAuthority {
  private constructor(private readonly conn: signalR.HubConnection) {}

  sessionId?: string;
  private _state: SessionState = "connecting";
  get state(): SessionState {
    return this._state;
  }

  /** Open streams by wire stream id. */
  readonly streams = new Map<number, RamphastosStream>();
  private pendingStreams = new Map<string, {
    resolve: (s: RamphastosStream) => void;
    reject: (e: RamphastosError) => void;
  }>();
  private invokeHandlers = new Set<InvokeHandler>();
  private streamRequestHandlers = new Set<StreamRequestHandler>();
  private streamListeners = new Set<(s: RamphastosStream) => void>();
  private stateListeners = new Set<(s: SessionState) => void>();
  private errorListeners = new Set<(e: RamphastosError) => void>();
  private readyListeners = new Set<() => void>();
  private opts: AuthorityConnectOptions = {};
  private intentionalClose = false;

  static async connect(url: string, opts: AuthorityConnectOptions = {}): Promise<RamphastosAuthority> {
    const conn = new signalR.HubConnectionBuilder()
      .withUrl(url, {
        httpClient: opts.httpClient,
        ...(opts.webSocketOnly
          ? { transport: signalR.HttpTransportType.WebSockets, skipNegotiation: true }
          : {}),
      })
      .withAutomaticReconnect()
      .build();

    const authority = new RamphastosAuthority(conn);
    authority.opts = opts;
    conn.on("message", (m: Envelope) => authority.dispatch(m));
    conn.on("frame", (raw: unknown) => authority.dispatchFrame(toBytes(raw)));
    conn.onreconnecting(() => authority.setState("reconnecting"));
    conn.onreconnected(() => authority.rehello());
    conn.onclose((err) => authority.transportClosed(err));

    authority.armReady();
    await conn.start();
    await authority.hello();
    await authority.ready;
    return authority;
  }

  // ---- lifecycle ----

  private setState(state: SessionState): void {
    if (this._state === state) return;
    this._state = state;
    this.stateListeners.forEach((cb) => cb(state));
  }

  private ready: Promise<void> = Promise.resolve();
  private markReady: () => void = () => {};
  private failReady: (e: unknown) => void = () => {};

  private armReady(): void {
    this.ready = new Promise<void>((resolve, reject) => {
      this.markReady = () => {
        this.setState("ready");
        this.readyListeners.forEach((cb) => cb());
        resolve();
      };
      this.failReady = reject;
    });
  }

  private transportClosed(err?: Error): void {
    const failure = new RamphastosError({
      code: "connection_closed",
      message: err?.message ?? "transport closed",
      fatal: true,
    });
    for (const [name, p] of this.pendingStreams) {
      this.pendingStreams.delete(name);
      p.reject(failure);
    }
    if (this.intentionalClose) return;
    this.setState("closed");
    this.failReady(failure);
  }

  private async hello(): Promise<void> {
    const hello: Envelope = {
      v: PROTOCOL_VERSION,
      type: "hello",
      auth: this.opts.auth,
      session: this.opts.sessionId ?? this.sessionId,
      role: "authority",
      meta: this.opts.meta,
    };
    stripUndefined(hello);
    await this.conn.send("Send", hello);
  }

  /**
   * Re-hello after an auto-reconnect; a fatal denial while "reconnecting"
   * is retried with backoff — on a restarted server the ownership mirror
   * may briefly lag the transport. Without the retry the socket stays up
   * but the session is silently dead.
   */
  private helloRetries = 0;

  private rehello(): void {
    void this.hello();
  }

  private scheduleHelloRetry(): void {
    if (this.helloRetries >= 8) return;
    const delay = Math.min(200 * 2 ** this.helloRetries++, 5000);
    setTimeout(() => {
      if (this._state === "reconnecting") this.rehello();
    }, delay);
  }

  // ---- inbound dispatch ----

  private dispatch(m: Envelope): void {
    switch (m.type) {
      case "welcome":
        this.helloRetries = 0;
        this.sessionId = m.session;
        this.markReady();
        break;
      case "invoke": {
        const invoke: ForwardedInvoke = {
          ref: m["id"] as string,
          path: m["path"] as string,
          args: (m["args"] as unknown[]) ?? [],
        };
        const cb = this.invokeHandlers.values().next().value;
        if (!cb) {
          this.respondError(invoke.ref, "invoke_failed", "no invoke handler");
          break;
        }
        Promise.resolve()
          .then(() => cb(invoke))
          .then((value) => this.respond(invoke.ref, value))
          .catch((err: unknown) =>
            this.respondError(invoke.ref, "invoke_failed",
              err instanceof Error ? err.message : String(err)));
        break;
      }
      case "stream-open": {
        // forwarded request (no streamId) — vs. peer announcement (with id).
        // != null covers explicit nulls from serializers that emit them.
        const streamId = m["streamId"] as number | undefined;
        if (streamId != null) {
          if (!this.streams.has(streamId)) {
            const s = this.makeStream(
              m["stream"] as string, streamId,
              (m["direction"] as StreamDirection) ?? "down");
            this.streamListeners.forEach((cb) => cb(s));
          }
          break;
        }
        const req: StreamRequest = {
          stream: m["stream"] as string,
          direction: (m["direction"] as StreamDirection) ?? "down",
          meta: m["meta"] as Record<string, unknown> | undefined,
        };
        void this.decideStreamRequest(req);
        break;
      }
      case "stream-opened": {
        const name = m["stream"] as string;
        const stream = this.makeStream(
          name, m["streamId"] as number,
          (m["direction"] as StreamDirection) ?? "down");
        const p = this.pendingStreams.get(name);
        if (p) {
          this.pendingStreams.delete(name);
          p.resolve(stream);
        }
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
      case "error": {
        const err = m["error"] as ErrorBody;
        if (err?.stream && this.pendingStreams.has(err.stream)) {
          const p = this.pendingStreams.get(err.stream)!;
          this.pendingStreams.delete(err.stream);
          p.reject(new RamphastosError(err));
        } else {
          this.errorListeners.forEach((cb) => cb(new RamphastosError(err)));
          if (err?.code === "authority_replaced") {
            // Superseded by another authority hello — drop the transport.
            // Reconnecting would just re-hello and start an ownership
            // ping-pong; intentionalClose keeps the state quiet (the app's
            // live instance owns the session now, or it will re-attach).
            this.intentionalClose = true;
            void this.conn.stop();
          }
          if (err?.fatal) {
            this.failReady(new RamphastosError(err));
            // No hello retry after authority_replaced — the transport is
            // intentionally stopped; re-hello would ping-pong ownership.
            if (this._state === "reconnecting" && err?.code !== "authority_replaced")
              this.scheduleHelloRetry();
          }
        }
        break;
      }
      // snapshot/patch/result are not meaningful for an authority — it owns
      // the truth and never consumes state.
    }
  }

  private async decideStreamRequest(req: StreamRequest): Promise<void> {
    const cb = this.streamRequestHandlers.values().next().value;
    if (!cb) {
      this.send({
        v: PROTOCOL_VERSION, type: "error", session: this.sessionId,
        error: { code: "stream_rejected", message: "no stream handler", stream: req.stream },
      });
      return;
    }
    try {
      const verdict = await cb(req);
      if (verdict === false) {
        this.send({
          v: PROTOCOL_VERSION, type: "error", session: this.sessionId,
          error: { code: "stream_rejected", message: "stream denied", stream: req.stream },
        });
      } else {
        const direction =
          typeof verdict === "object" && verdict?.direction ? verdict.direction : req.direction;
        this.send({
          v: PROTOCOL_VERSION, type: "stream-open", session: this.sessionId,
          stream: req.stream, direction, meta: req.meta,
        });
      }
    } catch (err) {
      this.send({
        v: PROTOCOL_VERSION, type: "error", session: this.sessionId,
        error: {
          code: "stream_rejected",
          message: err instanceof Error ? err.message : String(err),
          stream: req.stream,
        },
      });
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
      (frame) => void this.conn.send("SendFrame", toBase64(frame)),
      (reason) =>
        void this.send({
          v: PROTOCOL_VERSION, type: "stream-close", session: this.sessionId,
          stream: name, streamId, reason,
        }),
    );
    this.streams.set(streamId, stream);
    return stream;
  }

  // ---- state plane: push upstream ----

  /** Publish the full current state; the server stores it as the replica and
   *  fans a wrapped snapshot out to viewers. Call after connect/reconnect and
   *  whenever incremental patches don't apply (e.g. wholesale reload). */
  publishSnapshot(state: Record<string, unknown>): void {
    this.send({ v: PROTOCOL_VERSION, type: "snapshot", session: this.sessionId, root: state });
  }

  /** Publish incremental ops — paths relative to the state root. */
  publishPatch(ops: PatchOp[]): void {
    if (ops.length === 0) return;
    this.send({ v: PROTOCOL_VERSION, type: "patch", session: this.sessionId, ops });
  }

  // ---- command plane: client invokes forwarded here ----

  /**
   * Handle invokes forwarded from controllers. The handler's return value
   * resolves the invoke; a thrown error (or rejected promise) answers with
   * code "invoke_failed". First registered handler owns each invoke.
   */
  onInvoke(cb: InvokeHandler): () => void {
    this.invokeHandlers.add(cb);
    return () => this.invokeHandlers.delete(cb);
  }

  /** Manual respond — for handlers that complete out of band. */
  respond(ref: string, value: unknown): void {
    this.send({ v: PROTOCOL_VERSION, type: "result", session: this.sessionId, ref, value });
  }

  respondError(ref: string, code: string, message: string): void {
    this.send({
      v: PROTOCOL_VERSION, type: "result", session: this.sessionId,
      ref, error: { code, message },
    });
  }

  // ---- streams ----

  /**
   * Decide client stream-open requests. Return true (or {direction}) to
   * grant, false to deny, throw to deny with the error message. With no
   * handler registered requests are denied.
   */
  onStreamRequest(cb: StreamRequestHandler): () => void {
    this.streamRequestHandlers.add(cb);
    return () => this.streamRequestHandlers.delete(cb);
  }

  /** Authority-initiated stream — resolves once the server assigned the id. */
  openStream(
    name: string,
    opts: { direction?: StreamDirection; meta?: Record<string, unknown> } = {},
  ): Promise<RamphastosStream> {
    this.send({
      v: PROTOCOL_VERSION, type: "stream-open", session: this.sessionId,
      stream: name, direction: opts.direction ?? "down", meta: opts.meta,
    });
    return new Promise<RamphastosStream>((resolve, reject) =>
      this.pendingStreams.set(name, { resolve, reject }));
  }

  /** Subscribe to stream announcements (client-requested or self-opened). */
  onStream(cb: (s: RamphastosStream) => void): () => void {
    this.streamListeners.add(cb);
    return () => this.streamListeners.delete(cb);
  }

  // ---- subscriptions ----

  onStateChange(cb: (s: SessionState) => void): () => void {
    this.stateListeners.add(cb);
    return () => this.stateListeners.delete(cb);
  }

  /** Fires whenever the connection becomes (re-)attached and welcomed —
   *  a good moment to re-publish the snapshot. */
  onReady(cb: () => void): () => void {
    this.readyListeners.add(cb);
    return () => this.readyListeners.delete(cb);
  }

  onError(cb: (e: RamphastosError) => void): () => void {
    this.errorListeners.add(cb);
    return () => this.errorListeners.delete(cb);
  }

  private send(msg: Envelope): void {
    stripUndefined(msg);
    // A dead conn rejects every send — a superseded authority (or any
    // dropped transport) would otherwise surface unhandled rejections for
    // fire-and-forget publishes that are no-ops anyway.
    if (this.conn.state !== signalR.HubConnectionState.Connected) return;
    void this.conn.send("Send", msg).catch(() => {});
  }

  /** Drop the socket and reattach to the same session. */
  async reconnect(): Promise<void> {
    this.armReady();
    this.setState("reconnecting");
    this.intentionalClose = true;
    try {
      await this.conn.stop();
      await this.conn.start();
      await this.hello();
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
