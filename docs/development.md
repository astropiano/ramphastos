# Development

Working on Ramphastos itself: builds, tests, local dependencies, and
packaging. For the design, see [architecture.md](architecture.md).

## Requirements

- .NET 10 SDK (`net10.0` targets, `.slnx` solution format)
- Node.js 22+ for the client workspaces

## Build and test

```bash
dotnet build Ramphastos.slnx          # all server projects + example host
dotnet test src/Ramphastos.Core.Facts # xunit suite
npm ci && npm run build --workspaces  # ramphastos-client, ramphastos-react, example web
```

Package versions are centrally pinned in `Directory.Packages.props` —
add `PackageReference` without a version in the csproj and set the
`PackageVersion` once in the props file.

## Local dependencies: `RAMPHASTOS_LOCAL_DEPS`

`Ramphastos.Core` and `Ramphastos.TestSdk` consume `TotalSerializer` and
`TotalSerializer.ReflectionTools` as NuGet packages from nuget.org — that is
what a clean clone builds against.

To develop Ramphastos *together with* TotalSerializer — step into its source,
edit both repos, rebuild together — check it out as a sibling directory and
flip the env var:

```bash
# sibling checkouts: dev/ramphastos + dev/totalserializer
export RAMPHASTOS_LOCAL_DEPS=1
dotnet build Ramphastos.slnx
```

With the var set (any non-empty value), `src/Directory.Build.props` switches
the `PackageReference`s to `ProjectReference`s into
`../totalserializer/src/...`. Unset it to go back to the published packages.

If the checkout lives somewhere other than `../totalserializer`, override
the root:

```bash
RAMPHASTOS_LOCAL_DEPS=1 RamphastosLocalDepsRoot=/path/to/parent/ dotnet build
# or: dotnet build -p:RamphastosLocalDepsRoot=/path/to/parent/
```

Do not add local feeds or paths to the committed `nuget.config` — it is the
public-facing configuration and should stay nuget.org-only.

## Packaging

`src/Directory.Build.props` carries the shared NuGet metadata (version,
license expression, icon from `branding/`, embedded README, repository URL).
Per-project `Description`s live in each csproj. `Ramphastos.Core.Facts` is
marked `IsPackable=false`.

```bash
dotnet pack src -c Release -o packages-out
```

The npm packages (`@ramphastos/ramphastos-client`,
`@ramphastos/ramphastos-react`) build with `tsc -b` via their `prepack`
script; publish from each package directory with `npm publish`.

## Releasing

Releases are tag-driven: `git tag v0.3.0 && git push --tags` runs the CI
`publish` job, which packs `Ramphastos.*` to nuget.org (OIDC trusted
publishing) and versions + publishes both npm packages from the tag name.
The `Version` in `src/Directory.Build.props` is the development version —
tag builds override it.

## Layout

```
src/Ramphastos.Protocol/   wire contracts only, zero dependencies
src/Ramphastos.Core/       VM graph, notification engine, session actor
src/Ramphastos.Server/     ASP.NET Core, SignalR, auth, providers
src/Ramphastos.TestSdk/    in-memory test host + scripted test client
src/Ramphastos.Core.Facts/ test suite (not packed)
src/client/                npm workspaces (TS client + React bindings)
examples/hello-world/      minimal end-to-end app
scenarios/                 axn-dev docker scenarios (internal tooling)
```
