# TerrariaRuntime

A server-only TerrariaRuntime distribution for Terraria 1.4.6.1 and .NET 11.

This repository patches the official PC dedicated-server assembly into
`TerrariaRuntime.dll`, generates `TerrariaRuntime.Hooks.dll`, and creates one validation package
named `TerrariaRuntime.nupkg`. Runtime assembly names are `TerrariaRuntime` and
`TerrariaRuntime.Hooks` for low-level server integrations.

## Scope

The fork intentionally supports one target: the PC dedicated server.

- Target framework: `net11.0`
- SDK: `11.0.100`, pinned by `global.json`
- Terraria server: `1.4.6.1`
- Package ID: `TerrariaRuntime`
- Package version: `1.1.0`
- Runtime hooks: `MonoMod.RuntimeDetour 25.4.0-prerelease.3`

Client patching, graphics runtimes, mobile and tModLoader targets, and dynamic
patch plugins were removed. The remaining XNA-shaped shims are small managed
server compatibility and game-math types; no graphics runtime is built or
shipped.

## Build and validate

The patcher downloads the pinned official Terraria dedicated-server archive
when it is not already cached.

```bash
cd src
dotnet build TerrariaRuntime.Patcher/TerrariaRuntime.Patcher.csproj
dotnet TerrariaRuntime.Patcher/bin/Debug/net11.0/TerrariaRuntime.Patcher.dll
dotnet build TerrariaRuntime.Server.Launcher/TerrariaRuntime.Server.Launcher.csproj
dotnet TerrariaRuntime.Server.Launcher/bin/Debug/net11.0/TerrariaRuntime.Server.Launcher.dll -test-init
```

Successful patching produces:

- `src/TerrariaRuntime.Patcher/bin/Debug/net11.0/TerrariaRuntime.dll`
- `src/TerrariaRuntime.Patcher/bin/Debug/net11.0/TerrariaRuntime.Hooks.dll`
- `src/TerrariaRuntime.Patcher/bin/Debug/net11.0/TerrariaRuntime.nupkg`
- `src/TerrariaRuntime.Patcher/bin/Debug/net11.0/artifact-pc/`

NuGet publication uses GitHub Actions Trusted Publishing when a version tag matches
the built package. No long-lived NuGet API key is stored in the repository.

See [the .NET 11 baseline](docs/NET11.md) for dependency and validation rules.
See [the server integration boundaries](docs/SERVER-BOUNDARIES.md) for network overrides, world ownership and damage validation.

## Future hook work

Application-specific hook additions should remain narrow and measured. NPC creation,
spawn, transform, death and loot hooks already exist and provide the natural
boundary for a future NPC-AI optimization experiment. Do not move Terraria
mutable NPC state to worker threads; first profile AI families, then add the
smallest hook or pure snapshot boundary required by a measured optimization.

## Origin

TerrariaRuntime is derived from [Open Terraria API](https://github.com/SignatureBeef/Open-Terraria-API).
The inherited GPLv3 license and upstream author notices are retained.
