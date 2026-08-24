# Patch engine provenance

The initial implementation of this TerrariaRuntime-owned patch engine was derived from
the `ModFramework` and `ModFramework.Modules.CSharp` sources pinned by the
server-only fork before the external dependency was removed.

The source retains its original copyright and GPLv3 notices. It is maintained
here as part of TerrariaRuntime, under the repository's GPLv3 license, and is
not a runtime framework or a separately published package.

The embedded engine is intentionally limited to assembly rewriting, relinking,
hook emission, resource extraction, patch discovery and build-time C# patch
compilation required by the dedicated-server pipeline.

`HookGen/HookGenerator.cs` was copied from the MonoMod source revision formerly
pinned by this fork. Its upstream MIT notice is retained in the source file. It
is embedded because the modern HookGen implementation is not published as a
matching standalone NuGet package.
