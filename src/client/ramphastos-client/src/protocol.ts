export const PROTOCOL_VERSION = 1;

export type SessionRole = "viewer" | "controller" | "authority";
export type StreamDirection = "down" | "up" | "duplex";

export interface Envelope {
  v: number;
  type: string;
  session?: string;
  seq?: number;
  ack?: number;
  [key: string]: unknown;
}

export interface PatchOp {
  op: "add" | "remove" | "replace" | "move" | "copy" | "textpatch";
  path: string;
  value?: unknown;
  from?: string;
  patch?: string;
}

export interface ErrorBody {
  code: string;
  message: string;
  fatal?: boolean;
  /** Set when the error refers to a specific stream. */
  stream?: string;
}

export class RamphastosError extends Error {
  constructor(public readonly body: ErrorBody) {
    super(`${body.code}: ${body.message}`);
    this.name = "RamphastosError";
  }
}

/** Split an RFC 6901 pointer into unescaped segments. "" → []. */
export function parsePointer(path: string): string[] {
  if (!path || path === "/") return [];
  return path
    .split("/")
    .slice(1)
    .map((s) => s.replace(/~1/g, "/").replace(/~0/g, "~"));
}
