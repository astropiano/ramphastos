import type { StreamDirection } from "./protocol.js";

/**
 * Stream data-plane framing — identical bytes on every transport:
 *
 *   [0]    u8   flags/version (currently 0)
 *   [1..4] u32  streamId, little-endian
 *   [5..8] u32  per-stream seq, little-endian
 *   [9..]  payload
 *
 * SignalR's JSON protocol base64s the frame; MessagePack (or a raw binary
 * WebSocket transport) carries it verbatim — the codec doesn't care.
 */
const HEADER = 9;

export function encodeFrame(streamId: number, seq: number, payload: Uint8Array): Uint8Array {
  const frame = new Uint8Array(HEADER + payload.length);
  const view = new DataView(frame.buffer);
  frame[0] = 0;
  view.setUint32(1, streamId, true);
  view.setUint32(5, seq, true);
  frame.set(payload, HEADER);
  return frame;
}

export function decodeFrame(frame: Uint8Array): { streamId: number; seq: number; payload: Uint8Array } | null {
  if (frame.length < HEADER || frame[0] !== 0) return null;
  const view = new DataView(frame.buffer, frame.byteOffset, frame.byteLength);
  return {
    streamId: view.getUint32(1, true),
    seq: view.getUint32(5, true),
    payload: frame.subarray(HEADER),
  };
}

/**
 * Frames travel as a base64 *string* argument — the SignalR JSON protocol
 * cannot serialize raw ArrayBuffer args (they would stringify to {}), while
 * a base64 string binds byte[] on the server and survives any transport.
 */
export function toBase64(bytes: Uint8Array): string {
  let bin = "";
  for (let i = 0; i < bytes.length; i += 0x8000) {
    bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }
  return btoa(bin);
}

/** Normalize whatever the transport hands us into bytes. */
export function toBytes(raw: unknown): Uint8Array {
  if (raw instanceof Uint8Array) return raw;
  if (raw instanceof ArrayBuffer) return new Uint8Array(raw);
  if (typeof raw === "string") {
    // base64 (SignalR JSON protocol)
    const bin = atob(raw);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return bytes;
  }
  return new Uint8Array(0);
}

export interface ClientStreamHandlers {
  onData?: (payload: Uint8Array, seq: number) => void;
  onClose?: (reason?: string) => void;
}

/**
 * One open stream as the client sees it. `write` is only meaningful when the
 * negotiated direction allows upstream and the role is controller.
 */
export class RamphastosStream {
  /** per-stream client seq for upstream frames */
  private outSeq = 0;
  private handlers: ClientStreamHandlers = {};
  closed = false;
  closeReason?: string;

  constructor(
    public readonly name: string,
    public readonly streamId: number,
    public readonly direction: StreamDirection,
    private readonly sendFrame: (frame: Uint8Array) => void,
    private readonly sendClose: (reason?: string) => void,
  ) {}

  onData(cb: (payload: Uint8Array, seq: number) => void): this {
    this.handlers.onData = cb;
    return this;
  }

  onClose(cb: (reason?: string) => void): this {
    this.handlers.onClose = cb;
    return this;
  }

  /** Upstream write (client → server). */
  write(payload: Uint8Array | string): void {
    if (this.closed) return;
    const bytes = typeof payload === "string" ? new TextEncoder().encode(payload) : payload;
    this.sendFrame(encodeFrame(this.streamId, this.outSeq++, bytes));
  }

  close(reason?: string): void {
    if (this.closed) return;
    this.closed = true;
    this.sendClose(reason);
  }

  /** @internal — called by the session on inbound frames/closes. */
  _deliver(payload: Uint8Array, seq: number): void {
    this.handlers.onData?.(payload, seq);
  }

  /** @internal */
  _closed(reason?: string): void {
    this.closed = true;
    this.closeReason = reason;
    this.handlers.onClose?.(reason);
  }
}
