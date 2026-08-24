# VegaKernel .NET 11 server baseline

This document records the VegaKernel-specific runtime baseline and the compatibility debt intentionally retained while migrating the OTAPI PC server path from upstream 3.3.14 / Terraria 1.4.5.8.

## Scope

The first migration milestone is the PC dedicated-server path used by Vega. The authoritative target framework is `net11.0`, built with .NET SDK `11.0.100-preview.7.26381.103`.

The following projects are part of that baseline:

- `OTAPI.Common`
- `OTAPI.Patcher`
- `OTAPI.Scripts`
- `OTAPI.Server.Launcher`
- `examples/RuntimeExample.Server`
- the generated `OTAPI.dll` and `OTAPI.Runtime.dll` server package

The client launcher and client runtime example remain on their upstream baseline until they receive a separate client-focused migration and native/UI validation pass.

## Package identity

The fork publishes its PC server package as `VegaKernel.OTAPI`. Runtime assembly identities remain `OTAPI` and `OTAPI.Runtime` so consumers do not need an assembly-name fork.

The package target framework is derived from the executing patcher instead of being hard-coded in `NugetPackageBuilder`.

Patch-time dependencies are deliberately not exposed as dependencies of `VegaKernel.OTAPI`. The generated server package depends only on assemblies required while the patched Terraria server is running.

## MonoMod / ModFramework boundary

The upstream OTAPI stack combined `ModFramework 1.1.15`, legacy `MonoMod 22.7.31.1`, and a newer RuntimeDetour. To make those generations coexist, upstream rewrote `MonoMod.dll` with Cecil at patcher startup.

The VegaKernel PC server path removes that architecture:

- legacy `MonoMod 22.7.31.1` is not a server patcher or runtime dependency;
- the `PatchTargets.PatchMonoMod()` self-modifying compatibility hack is removed;
- patching uses `MonoMod.Patcher 25.0.1`;
- runtime detours use `MonoMod.RuntimeDetour 25.3.6`;
- the current upstream `ModFramework` source is pinned as a build-time submodule and compiled against `MonoMod.Patcher 25.0.1`;
- only the C# patch-loader portion required to compile the existing OTAPI patch scripts is built for the server toolchain;
- Lua and ClearScript ModFramework modules are not part of the VegaKernel PC server path;
- modern HookGen source is pinned from the MonoMod .NET 11 compatibility branch instead of consuming the legacy HookGen package;
- the dedicated-server runtime no longer initializes or exposes the ModFramework runtime plugin loader.

ModFramework is therefore a temporary source-level patch-time adapter, not a runtime framework and not a published VegaKernel dependency. This preserves OTAPI's mature relinker and script-loader implementation without forcing VegaKernel to reimplement it while the patch scripts are gradually made less dependent on ModFramework-specific APIs.

### Remaining .NET 11 compatibility exception

Published MonoMod 25.x packages still ship a build-transitive target that rejects `.NETCoreApp` TFMs newer than `net10.0`. Upstream .NET 11 support is being implemented in MonoMod PR #307 but is not released yet.

The repository therefore sets:

```xml
<MonoMod_ReallySkipCheckTargetRuntime>true</MonoMod_ReallySkipCheckTargetRuntime>
```

This opt-in is temporary, explicit and must not be silently duplicated elsewhere. Remove it as soon as a released MonoMod version officially accepts `net11.0`, then run the complete server qualification suite before updating the fork baseline.

## Dedicated-server runtime boundary

Vega owns its module/plugin lifecycle. OTAPI must not provide a second runtime plugin system on the Vega dedicated-server path.

Consequently:

- `Terraria.Program` retains OTAPI lifecycle and hook startup but does not create a server `ModContext`;
- the server does not scan or execute a ModFramework `modifications/` directory;
- `OTAPI.Server.Launcher` does not reference ModFramework, MonoMod.Patcher, Lua or ClearScript modules;
- `examples/RuntimeExample.Server` demonstrates direct `On.*` hook registration and explicit host-managed initialization/shutdown.

Client and tModLoader behavior is intentionally not redesigned by this server-first migration.

## FNA decision

FNA is used by OTAPI client variants; the dedicated server uses the XNA shims under `OTAPI.Scripts/Shims/Xna`.

The existing FNA submodule is pinned to `ebadfa609c510206da7f872c24325fae5bd802a5` (2024-12-22). At the time of this migration the FNA upstream `master` head is `78f1d6570b70e488e639b897f22030fc41849e8f` (2026-08-22), 237 commits ahead. Both revisions still target `net8.0` in `FNA.Core.csproj`.

`OTAPI.Patcher` still compiles the PC client target and therefore has a compile-time reference to FNA, but FNA is not part of the generated PC dedicated-server runtime package.

Updating FNA does not unblock the .NET 11 PC server migration and would mix a large graphics/audio/native dependency update into a server-runtime change. Keep the current pin for this milestone. Re-evaluate and test FNA separately when migrating the client launcher.

## Validation gates

Do not merge the .NET 11 migration merely because restore and compilation succeed. Before the branch becomes the fork baseline, verify all of the following:

- restore and build with the pinned SDK on Windows, Linux and macOS;
- prove the source-built ModFramework uses `MonoMod.Patcher 25.0.1` and does not restore `MonoMod 22.x`;
- patch Terraria 1.4.5.8 successfully;
- generate `OTAPI.dll`, `OTAPI.Runtime.dll` and `VegaKernel.OTAPI` with `lib/net11.0` assets;
- verify generated `OTAPI.dll` has no dedicated-server assembly reference to `ModFramework` or `MonoMod.Patcher`;
- boot `OTAPI.Server.Launcher -test-init` on every CI operating system;
- inspect the package dependency graph for accidental duplicate MonoMod generations;
- load the package from a clean `net11.0` consumer;
- run Vega's OTAPI hook, world-load/save, networking and shutdown smoke tests against the fork package;
- retain the upstream 3.3.14 commit SHA as the rebase point for future upstream syncs.

## Deferred work

The following are deliberately outside the first server migration gate:

- client launcher retargeting and client runtime-plugin redesign;
- FNA refresh;
- mobile and tModLoader qualification;
- replacing the remaining ModFramework patch-time adapter after the server scripts no longer need its relinker/script-loader extensions;
- Vega-specific NPC/performance patch points.

Those changes should land independently so regressions can be attributed and rebases against upstream remain reviewable.
