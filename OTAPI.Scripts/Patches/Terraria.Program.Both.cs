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
#pragma warning disable CS0626 // Method, operator, or accessor is marked external and has no attributes on it
#pragma warning disable CS0436 // Type conflicts with imported type

#if Terraria || tModLoader
using ModFramework;
#endif
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Terraria
{
    partial class patch_Program
    {
#if !Terraria
        public static extern void orig_DisplayException(Exception e);
        public static void DisplayException(Exception e)
        {
            Console.WriteLine(e.ToString());
        }
#endif

        /// <summary>
        /// Triggers when consumers should start attaching events. At this point assembly resolution is ready.
        /// </summary>
        public static event EventHandler? OnLaunched;

#if Terraria || tModLoader
        // Client variants retain the upstream runtime ModFramework plugin system.
        // Dedicated Vega servers use Vega's own module system and must not carry this runtime dependency.
        public static ModContext ModContext { get; } = new ModContext("OTAPI");
#endif

        static string CSV(params string?[] args) => string.Join(",", args.Where(x => !string.IsNullOrWhiteSpace(x)));

        private static void Configure()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                var path = Path.Combine(
                    Environment.CurrentDirectory,
                    Environment.Is64BitProcess ? "x64" : "x86"
                );
                Directory.CreateDirectory(path);

                try
                {
                    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
                    AddDllDirectory(path);
                }
                catch
                {
                    SetDllDirectory(path);
                }
            }

#if Terraria || tModLoader
            Environment.SetEnvironmentVariable("FNA_GRAPHICS_ENABLE_HIGHDPI", "1");
#endif
        }

        /// <summary>
        /// Initializes OTAPI runtime hooks. Client variants additionally initialize the legacy ModFramework runtime plugin system.
        /// </summary>
        public static void LaunchOTAPI()
        {
            Configure();

#if Terraria || tModLoader
            var modFrameworkVersion = $"ModFw:{OTAPI.Common.ModFramework.VersionShort}";
#else
            string? modFrameworkVersion = null;
#endif
            Console.WriteLine($"[OTAPI] Starting up ({CSV(
                OTAPI.Common.Target.Replace("OTAPI", "").Trim(),
                OTAPI.Common.VersionShort,
                modFrameworkVersion
            )}).");

#if Terraria || tModLoader
            ModContext.Parameters.Add(Assembly.GetExecutingAssembly());

            const string modificationsPath = "modifications";
            if (Directory.Exists(modificationsPath))
            {
                ModContext.PluginLoader.AddFromFolder(modificationsPath, searchOption: SearchOption.AllDirectories);
            }
            ModContext.Apply(ModType.Runtime);
#endif

#if Terraria
            Main.versionNumber += " OTAPI";
            Main.versionNumber2 += " OTAPI";
#endif

            OnLaunched?.Invoke(null, EventArgs.Empty);
        }

        public static void ShutdownOTAPI()
        {
#if Terraria || tModLoader
            ModContext.Apply(ModType.Shutdown);
#endif
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetDefaultDllDirectories(int directoryFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern void AddDllDirectory(string lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetDllDirectory(string lpPathName);

        const int LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;

#if tModLoaderServer || tModLoader
        public static extern void orig_LaunchGame_(bool isServer);
        public static void LaunchGame_(bool isServer)
        {
            LaunchOTAPI();
            orig_LaunchGame_(isServer);
            ShutdownOTAPI();
        }
#else
#if Terraria_1442_OrAbove || TerrariaServer_1442_OrAbove
        public static extern void orig_RunGame();
        public static void RunGame()
        {
            LaunchOTAPI();
            orig_RunGame();
            ShutdownOTAPI();
        }
#else
        public static extern void orig_LaunchGame(string[] args, bool monoArgs = false);
        public static void LaunchGame(string[] args, bool monoArgs = false)
        {
            LaunchOTAPI();
            orig_LaunchGame(args, monoArgs);
            ShutdownOTAPI();
        }
#endif
#endif
    }
}

namespace OTAPI
{
    [MonoMod.MonoModIgnore]
    public static partial class Common
    {
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor.
        public static readonly string Target;
        public static readonly string Version;
        public static readonly string VersionShort;
        public static readonly string GitHubCommit;

#if Terraria || tModLoader
        public static partial class ModFramework
        {
            public static readonly string Version;
            public static readonly string VersionShort;
        }
#endif
#pragma warning restore CS8618
    }
}
