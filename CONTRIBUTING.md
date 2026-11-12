# Contributing

Ramphastos is alpha-stage and feedback-driven — issues, design discussion,
and PRs are welcome. Read [docs/architecture.md](docs/architecture.md)
first: the wire protocol is the product, so changes that touch the envelope,
patch semantics, roles, or streams are protocol changes and need discussion
before code.

## Getting set up

```bash
dotnet build Ramphastos.slnx
dotnet test src/Ramphastos.Core.Facts
npm ci && npm run build --workspaces
```

See [docs/development.md](docs/development.md) for the full picture —
including `RAMPHASTOS_LOCAL_DEPS` if you're working on
[TotalSerializer](https://github.com/astropiano/totalserializer) alongside.

## Ground rules

- **Protocol compatibility matters.** The `v` envelope field, message types,
  patch ops, roles, and stream semantics are a versioned contract — don't
  change them casually.
- **Keep `Ramphastos.Protocol` dependency-free.** Wire contracts only.
- **One actor turn = one patch batch.** Server-side state changes must stay
  inside the session actor's serialized execution; don't introduce locks or
  cross-session mutation paths.
- Match existing style: file-scoped namespaces, `Nullable` enabled, compact
  code, no comment noise.
- Tests live in `src/Ramphastos.Core.Facts` (xunit + FluentAssertions via
  `Ramphastos.TestSdk`). Add facts for behavioral changes.

## Pull requests

- Small, focused PRs over big-bang changes.
- Describe the *why*, especially for protocol or concurrency changes.
- CI must be green: `dotnet build`, `dotnet test`, npm workspace builds.
