# .NET 11 dedicated-server baseline

This TerrariaRuntime 1.1.0 distribution is server-only and targets Terraria 1.4.6.1.
The authoritative target is `net11.0` with SDK
`11.0.100` and package ID `TerrariaRuntime`.

## Removed legacy surface

The fork no longer contains or supports:

- PC client patching or the Avalonia client launcher;
- FNA and client graphics/audio/input integration;
- mobile and tModLoader patch targets;
- Lua, JavaScript or runtime C# plugin loading;
- the former external patch-framework project, runtime API and submodule;
- MonoMod 22.x compatibility rewriting and the old HookGen package.

The official dedicated server still exposes some XNA-shaped value types in
gameplay signatures. `src/TerrariaRuntime.Scripts/Shims/Xna` supplies those small managed
types without FNA or a graphics runtime. The server map subsystem is removed by
the dedicated-server patch set. Client-only resource-pack metadata is stripped;
localization, world-generation metadata and Creative-mode data remain because
dedicated-server code consumes them.

## Patch engine

`TerrariaRuntime.PatchEngine` is a private, non-packable build tool owned by this
repository. It contains the relinking, hook emission, resource extraction,
patch discovery and build-time C# compilation needed by the retained server
patches. The original GPLv3 notices are retained.

The generated `TerrariaRuntime.dll` and `TerrariaRuntime.Hooks.dll` must not reference
`TerrariaRuntime.PatchEngine`, patch-time MonoMod components, FNA or a client launcher.
`NugetPackageBuilder` enforces the patch-time dependency part of this boundary
before creating a package.

HookGen source is embedded from the formerly pinned modern MonoMod revision
because NuGet.org only publishes the obsolete standalone HookGen generation.
The source retains MonoMod's MIT notice.

## MonoMod .NET 11 exception

The build-time `MonoMod.Patcher 25.0.1` package predates .NET 11, so the build
retains its package-guard override. At runtime, `MonoMod.RuntimeDetour
25.4.0-prerelease.3` and `MonoMod.Core 1.4.0-prerelease.3` provide `net11.0`
assets. The old locally rebuilt Preview 7 core is no longer used:

```xml
<MonoMod_ReallySkipCheckTargetRuntime>true</MonoMod_ReallySkipCheckTargetRuntime>
```

This is accepted only with the real patch, generated-assembly inspection and
dedicated-server boot smoke. Remove the override when an official MonoMod
package declares .NET 11 support.

## Validation gates

Every change to the fork must:

1. restore and build the patcher with the pinned .NET 11 SDK;
2. download/patch the pinned official Terraria server successfully;
3. generate `TerrariaRuntime.dll`, `TerrariaRuntime.Hooks.dll` and `TerrariaRuntime.nupkg`;
4. reject patch-time, FNA and client references in generated assemblies;
5. build `TerrariaRuntime.Server.Launcher` and complete its `-test-init` smoke;
6. load the local package from a clean `net11.0` consumer;
7. run consumer world/network/hook/shutdown tests before changing the package
   reference.

NuGet.org publication is available only through the tag-triggered Trusted
Publishing workflow after these gates pass.

## Repository history

This fork uses a new server-only root history. The full inherited upstream
history is kept outside the repository in the maintainer's pre-trim Git bundle.
Future upstream intake should be performed as reviewed patch/cherry-pick work,
not by restoring client/mobile ancestry to the default branch.
