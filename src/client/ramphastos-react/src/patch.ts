import { parsePointer, PatchOp } from "./protocol.js";

/**
 * Immutable patch application: every op rebuilds the ancestor chain of the
 * touched path (copy-on-write), so subscribers can rely on reference
 * identity for memoization — required for React's re-render model.
 */

type Mutator = (parent: any, key: string) => any;

function withChild(node: any, key: string, child: any): any {
  const next = Array.isArray(node) ? node.slice() : { ...node };
  if (Array.isArray(next)) next[Number(key)] = child;
  else next[key] = child;
  return next;
}

function updateIn(node: any, segs: string[], i: number, fn: Mutator): any {
  if (i === segs.length - 1) return fn(node, segs[i]);
  const child = node?.[segs[i]];
  if (child === undefined || child === null)
    throw new Error(`Patch path broken at '${segs.slice(0, i + 1).join("/")}'`);
  return withChild(node, segs[i], updateIn(child, segs, i + 1, fn));
}

function readAt(doc: any, segs: string[]): unknown {
  let node: any = doc;
  for (const s of segs) node = node?.[s];
  return node;
}

const addOp =
  (value: unknown): Mutator =>
  (parent: any, key: string) => {
    if (Array.isArray(parent)) {
      const a = parent.slice();
      if (key === "-") a.push(value);
      else a.splice(Number(key), 0, value);
      return a;
    }
    return { ...parent, [key]: value };
  };

const replaceOp =
  (value: unknown): Mutator =>
  (parent: any, key: string) => {
    // RFC 6902: unlike add, replace OVERWRITES the index on arrays.
    if (Array.isArray(parent)) {
      const a = parent.slice();
      a[Number(key)] = value;
      return a;
    }
    return { ...parent, [key]: value };
  };

const removeOp: Mutator = (parent: any, key: string) => {
  if (Array.isArray(parent)) {
    const a = parent.slice();
    a.splice(Number(key), 1);
    return a;
  }
  const o = { ...parent };
  delete o[key];
  return o;
};

/**
 * Apply one RFC 6902 patch batch (plus "textpatch") to the mirrored graph.
 * Returns a NEW root; untouched branches keep their identity.
 */
export function applyPatch(doc: any, ops: PatchOp[]): any {
  let root = doc;
  for (const op of ops) {
    const segs = parsePointer(op.path);
    switch (op.op) {
      case "add":
        root = updateIn(root, segs, 0, addOp(op.value));
        break;
      case "replace":
      case "textpatch":
        // diff_match_patch lands with the TextSync feature; until then the
        // server never emits textpatch — fall back to replace semantics.
        root = updateIn(root, segs, 0, replaceOp(op.value));
        break;
      case "remove":
        root = updateIn(root, segs, 0, removeOp);
        break;
      case "move": {
        const fromSegs = parsePointer(op.from!);
        const value = readAt(root, fromSegs);
        root = updateIn(root, fromSegs, 0, removeOp);
        root = updateIn(root, segs, 0, addOp(value));
        break;
      }
      case "copy": {
        const value = structuredClone(readAt(root, parsePointer(op.from!)));
        root = updateIn(root, segs, 0, addOp(value));
        break;
      }
    }
  }
  return root;
}
