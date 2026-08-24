using TerrariaRuntime.PatchEngine;
using MonoMod;
using System;
using System.Linq;

/// <summary>
/// @doc Removes data loaded exclusively by the graphical client's content initialization.
/// </summary>
[Patch(PatchStage.PreMerge, "Pruning client-only resources")]
[MonoMod.MonoModIgnore]
void PruneClientResources(MonoModder modder)
{
    const string resourcePackCatalog = "Terraria.IO.Data.ResourcePacksDefaultInfo.tsv";
    var resource = modder.Module.Resources.Single(entry =>
        string.Equals(entry.Name, resourcePackCatalog, StringComparison.Ordinal));

    modder.Module.Resources.Remove(resource);
}
