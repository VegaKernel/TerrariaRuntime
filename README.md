# Open Terraria API

A server-only Open Terraria API distribution for Terraria 1.4.5.8 and .NET 11.

This repository patches the official PC dedicated-server assembly into
`OTAPI.dll`, generates `OTAPI.Runtime.dll`, and creates one validation package
named `OTAPI.nupkg`. Runtime assembly names remain `OTAPI` and
`OTAPI.Runtime` for low-level server integrations.

## Scope

The fork intentionally supports one target: the PC dedicated server.

- Target framework: `net11.0`
- SDK: `11.0.100-preview.7.26381.103`, pinned by `global.json`
- Terraria server: `1.4.5.8`
- Package ID: `OTAPI`
- Package version: `4.0.0`
- Runtime hooks: `MonoMod.RuntimeDetour 25.3.6`

Client patching, graphics runtimes, mobile and tModLoader targets, and dynamic
patch plugins were removed. The remaining XNA-shaped shims are small managed
server compatibility and game-math types; no graphics runtime is built or
shipped.

## Build and validate

The patcher downloads the pinned official Terraria dedicated-server archive
when it is not already cached.

```bash
cd src
dotnet build OTAPI.Patcher/OTAPI.Patcher.csproj
dotnet OTAPI.Patcher/bin/Debug/net11.0/OTAPI.Patcher.dll
dotnet build OTAPI.Server.Launcher/OTAPI.Server.Launcher.csproj
dotnet OTAPI.Server.Launcher/bin/Debug/net11.0/OTAPI.Server.Launcher.dll -test-init
```

Successful patching produces:

- `src/OTAPI.Patcher/bin/Debug/net11.0/OTAPI.dll`
- `src/OTAPI.Patcher/bin/Debug/net11.0/OTAPI.Runtime.dll`
- `src/OTAPI.Patcher/bin/Debug/net11.0/OTAPI.nupkg`
- `src/OTAPI.Patcher/bin/Debug/net11.0/artifact-pc/`

NuGet publication uses GitHub Actions Trusted Publishing when a matching
`v4.0.0` tag is pushed. No long-lived NuGet API key is stored in the repository.

See [the .NET 11 baseline](docs/NET11.md) for dependency and validation rules.

## Future hook work

Application-specific hook additions should remain narrow and measured. NPC creation,
spawn, transform, death and loot hooks already exist and provide the natural
boundary for a future NPC-AI optimization experiment. Do not move Terraria
mutable NPC state to worker threads; first profile AI families, then add the
smallest hook or pure snapshot boundary required by a measured optimization.