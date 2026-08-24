/*
Copyright (C) 2020-2024 SignatureBeef

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

namespace RuntimeExample.Server;

/// <summary>
/// Minimal server consumer example. The host calls <see cref="Initialize"/> during its own module startup.
/// OTAPI no longer provides a second runtime plugin loader on the Vega dedicated-server path.
/// </summary>
public static class Hooks
{
    private static bool initialized;

    public static void Initialize()
    {
        if (initialized)
            return;

        initialized = true;
        Console.WriteLine("[RuntimeExample Server] Initializing direct OTAPI hooks");
        On.Terraria.NetMessage.greetPlayer += GreetPlayer;
    }

    public static void Shutdown()
    {
        if (!initialized)
            return;

        On.Terraria.NetMessage.greetPlayer -= GreetPlayer;
        initialized = false;
    }

    private static void GreetPlayer(On.Terraria.NetMessage.orig_greetPlayer orig, int playerIndex)
    {
        Console.WriteLine($"[RuntimeExample Server] Greet player: {playerIndex}");
        orig(playerIndex);
    }
}
