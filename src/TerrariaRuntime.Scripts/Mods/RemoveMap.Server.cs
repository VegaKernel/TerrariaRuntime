/*
Copyright (C) 2020 DeathCradle

This file is part of Open Terraria API v3 (OTAPI)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <http://www.gnu.org/licenses/>.
*/

using TerrariaRuntime.PatchEngine;
using MonoMod;

/// <summary>
/// @doc Disables the world map to be created on the server (it is not needed, at least on vanilla).
/// </summary>
[Patch(PatchStage.PreMerge, "Removing world map")]
[MonoMod.MonoModIgnore]
void RemoveMap(MonoModder modder)
{
	var worldMap = modder.GetDefinition<Terraria.Map.WorldMap>();
	foreach (var method in worldMap.Methods)
	{
		method.ClearBody();
	}
}
