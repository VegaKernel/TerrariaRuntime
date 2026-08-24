# VegaKernel .NET 11 server baseline

This document records the VegaKernel-specific runtime baseline and the compatibility debt intentionally retained while migrating the OTAPI PC server path from upstream 3.3.14 / Terraria 1.4.5.8.

## Scope

The first migration milestone is the PC dedicated-server path used by Vega. The authoritative target framework is `net11.0`, built with `.NET SDK 11.0.100-preview.7.26381.103`.

The following projects are part of that baseline:

- `OTAPI.Common`
- `OTAPI.Patcher`
- `OTAPI.Scripts`
- `OTAPI.Server.Launcher`
- `examples/RuntimeExample.Server`
- the generated `OTAPI.dll` and `OTAPI.Runtime.dll` server package

The client launcher and client runtime example remain on `net9.0` until they receive a separate client-focused migration and native/UI validation pass.

## Package identity

The fork publishes its PC server package as `VegaKernel.OTAPI`. Runtime assembly identities remain `OTAPI` and `OTAPI.Runtime` so consumers do not need an assembly-name fork.

The package target framework is derived from the executing patcher instead of being hard-coded in `NugetPackageBuilder`.

## MonoMod / ModFramework boundary

`ModFramework 1.1.15` still targets `net9.0` and depends on the legacy `MonoMod >= 22.7.31.1` package. OTAPI also needs a current RuntimeDetour implementation for modern CoreCLR runtimes. Upstream currently bridges that split by rewriting parts of `MonoMod.dll` at patcher startup in `PatchTargets.PatchMonoMod()`.

VegaKernel does not treat that runtime rewrite as a desirable final architecture. It remains an explicit compatibility exception until ModFramework can move from the legacy `MonoMod` package to the current MonoMod patcher/component model. Do not hide or duplicate this workaround with binding redirects or additional assembly rewrites.

The current server migration may update `MonoMod.RuntimeDetour` independently, but removal of the legacy `MonoMod` dependency must be validated together with a ModFramework migration.

## FNA decision

FNA is used by OTAPI client variants; the dedicated server uses the XNA shims under `OTAPI.Scripts/Shims/Xna`.

The existing FNA submodule is pinned to `ebadfa609c510206da7f872c24325fae5bd802a5` (2024-12-22). At the time of this migration the FNA upstream `master` head is `78f1d6570b70e488e639b897f22030fc41849e8f` (2026-08-22), 237 commits ahead. Both revisions still target `net8.0` in `FNA.Core.csproj`.

Updating FNA therefore does not unblock the .NET 11 PC server migration and would mix a large graphics/audio/native dependency update into a server-runtime change. Keep the current pin for this milestone. Re-evaluate and test FNA separately when migrating the client launcher.

## Validation gates

Do not merge the .NET 11 migration merely because restore and compilation succeed. Before the branch becomes the fork baseline, verify all of the following:

- restore and build with the pinned SDK on Windows, Linux and macOS;
- patch Terraria 1.4.5.8 successfully;
- generate `OTAPI.dll`, `OTAPI.Runtime.dll` and `VegaKernel.OTAPI` with `lib/net11.0` assets;
- boot `OTAPI.Server.Launcher -test-init` on every CI operating system;
- inspect the package dependency graph for accidental duplicate MonoMod generations;
- load the package from a clean `net11.0` consumer;
- run Vega's OTAPI hook, world-load/save, networking and shutdown smoke tests against the fork package;
- retain the upstream 3.3.14 commit SHA as the rebase point for future upstream syncs.

## Deferred work

The following are deliberately outside the first server migration gate:

- client launcher retargeting;
- FNA refresh;
- mobile and tModLoader targets;
- replacing ModFramework or the legacy MonoMod patcher stack;
- Vega-specific NPC/performance patch points.

Those changes should land independently so regressions can be attributed and rebases against upstream remain reviewable.
