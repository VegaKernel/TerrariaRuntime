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

using System;
using TerrariaRuntime.PatchEngine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod;
using Terraria;
using Terraria.DataStructures;
using Microsoft.Xna.Framework;

/// <summary>
/// @doc Creates Hooks.NPC.DropLoot. Allows plugins to alter or cancel NPC loot drops.
/// </summary>
[Patch(PatchStage.PreMerge, "Hooking Terraria.GameContent.ItemDropRules.CommonCode.DropItemFromNPC")]
[MonoMod.MonoModIgnore]
void HookNpcLoot(MonoModder modder)
{
    var NewNPC = modder.GetILCursor(() => Terraria.GameContent.ItemDropRules.CommonCode.DropItemFromNPC(default, default, default, default));

    NewNPC.GotoNext(
        i => i.OpCode == OpCodes.Call && i.Operand is MethodReference methodReference && methodReference.Name == "NewItem" && methodReference.DeclaringType.FullName == "Terraria.Item"
    );

    NewNPC.Emit(OpCodes.Ldarg_0); // NPC instance
    NewNPC.Next.Operand = modder.GetMethodDefinition(() => TerrariaRuntime.Hooks.NPC.InvokeDropLoot(null, 0, 0, 0, 0, 0, 0, false, 0, NewItemOwnership.None, null, null, null));
}

namespace TerrariaRuntime
{
    public static partial class Hooks
    {
        public static partial class NPC
        {
            public class DropLootEventArgs : EventArgs
            {
                public HookEvent Event { get; set; }
                public HookResult? Result { get; set; }

                public Terraria.DataStructures.IEntitySource Source { get; set; }
                public Terraria.NPC Npc { get; set; }
                public int ItemIndex { get; set; }
                public int X { get; set; }
                public int Y { get; set; }
                public int Width { get; set; }
                public int Height { get; set; }
                public int Type { get; set; }
                public int Stack { get; set; }
                public bool NoBroadcast { get; set; }
                public int Pfix { get; set; }

                [Obsolete("NoGrabDelay is no longer used in Terraria 1.4.5.7 and above, but is kept for API compatibility.")]
                public bool NoGrabDelay { get; set; }
                [Obsolete("ReverseLookup is no longer used in Terraria 1.4.5 and above, but is kept for API compatibility.")]
                public bool ReverseLookup { get; set; }
                public NewItemOwnership Ownership { get; set; }
                public Vector2? Velocity { get; set; }
                public Item.NewItemModifier Modifier { get; set; }
            }
            public static event EventHandler<DropLootEventArgs>? DropLoot;

            public static int InvokeDropLoot(Terraria.DataStructures.IEntitySource source, int X, int Y, int Width, int Height, int Type,

                int stack, bool noBroadcast, int prefix, NewItemOwnership ownership, Vector2? velocity, Item.NewItemModifier modifier,
                Terraria.NPC instance)
            {
                var args = new DropLootEventArgs()
                {
                    Event = HookEvent.Before,
                    Source = source,
                    X = X,
                    Y = Y,
                    Width = Width,
                    Height = Height,
                    Type = Type,
                    NoBroadcast = noBroadcast,
                    Stack = stack,
                    Pfix = prefix,
                    Ownership = ownership,
                    Velocity = velocity,
                    Modifier = modifier,
                    NoGrabDelay = false, // no longer used, but kept for api compatibility.
                    ReverseLookup = false, // no longer used, but kept for api compatibility.
                    Npc = instance,

                    ItemIndex = 0,
                };
                DropLoot?.Invoke(null, args);
                if (args.Result != HookResult.Cancel)
                {
                    args.ItemIndex = Terraria.Item.NewItem(args.Source, args.X, args.Y, args.Width, args.Height, args.Type, args.Stack, args.NoBroadcast, args.Pfix, args.Ownership, args.Velocity, args.Modifier);
                    args.Event = HookEvent.After;
                    DropLoot?.Invoke(null, args);
                }
                return args.ItemIndex;
            }
        }
    }
}

