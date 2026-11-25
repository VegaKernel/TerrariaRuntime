# Historical MonoMod.Core Preview 7 override

`MonoMod.Core.dll` is built from MonoMod pull request 307, commit
`d2b9d9bbb12210adc0f3eb5ee20b03ea0577d9cf`, targeting `net10.0` for
runtime compatibility. The pull request adds the CoreCLR 11 JIT interface and
the runtime-async validation used by TerrariaRuntime 1.0.1.

The assembly identity is normalized to published `MonoMod.Core 1.3.6.0`, and
its `MonoMod.Utils` reference to `25.0.14.0`, so it can replace the transitive
runtime from `MonoMod.RuntimeDetour 25.3.6` without binding conflicts.
TerrariaRuntime 1.1.0 no longer copies or packages this binary. The runtime
uses the .NET 11 assets from the published MonoMod packages instead.

Upstream: https://github.com/MonoMod/MonoMod/pull/307
