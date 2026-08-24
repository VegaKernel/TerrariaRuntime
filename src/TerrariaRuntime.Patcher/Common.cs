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
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace TerrariaRuntime.Patcher;

[MonoMod.MonoModIgnore]
public static partial class Common
{
    public static string GetVersion()
    {
        var attr = typeof(Common).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>() ??
            throw new Exception("Unable to get version attribute");
        return attr.InformationalVersion;
    }

    public static void Log(string message)
    {
        Console.WriteLine($"[PatchEngine] {message}");
    }

    public static string? GetGitCommitSha()
    {
        var commitSha = Environment.GetEnvironmentVariable("GITHUB_SHA")?.Trim();
        if (commitSha != null && commitSha.Length >= 7)
        {
            return commitSha.Substring(0, 7);
        }
        return null;
    }

    public static async Task<string> DownloadZipAsync(string url)
    {
        Console.WriteLine($"Downloading {url}");
        var uri = new Uri(url);
        string filename = Path.GetFileName(uri.AbsolutePath);
        if (!String.IsNullOrWhiteSpace(filename))
        {
            var savePath = Path.Combine(Environment.CurrentDirectory, filename);

            if (!File.Exists(savePath))
            {
                using var client = new HttpClient();
                var data = await client.GetByteArrayAsync(url);
                File.WriteAllBytes(savePath, data);
            }

            return savePath;
        }
        else throw new NotSupportedException();
    }

    public static string ExtractZip(string zipPath)
    {
        var directory = Path.GetFileNameWithoutExtension(zipPath);
        var info = new DirectoryInfo(directory);
        Console.WriteLine($"Extracting to {directory}");

        if (info.Exists) info.Delete(true);

        info.Refresh();

        if (!info.Exists || info.GetDirectories().Length == 0)
        {
            info.Create();
            ZipFile.ExtractToDirectory(zipPath, directory);
        }

        return directory;
    }

    public static void AddMarkdownFormatter()
    {
        const string RepoBase = "https://github.com/VegaKernel/TerrariaRuntime/tree/main";
        const string Scripts = "/src/TerrariaRuntime.Scripts";
        const string HeaderFormat = "| Type | Patch | Comment |\n| ---- | ----- | ------- |";
        MarkdownDocumentor.RegisterTypeFormatter<BasicComment>((header, data) =>
        {
            if (header) return HeaderFormat;

            if (data?.FilePath is null) throw new Exception("Unable to determine ");
            var filename = Path.GetFileName(data.FilePath);
            var basePath = "";
            if (data.Type == "toplevel")
                basePath = RepoBase + Scripts + "/Mods";
            else if (data.Type == "patch")
                basePath = RepoBase + Scripts + "/Patches";
            else if (data.Type == "module")
            {
                basePath = RepoBase + Scripts + "/Shims";
                filename = Path.GetFileName(Path.GetDirectoryName(data.FilePath));
            }
            return $"| {data.Type} | [{filename}]({basePath}/{filename}) | {data.Comments} |";
        });

    }
}
