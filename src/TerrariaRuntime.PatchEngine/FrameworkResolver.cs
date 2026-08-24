using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace TerrariaRuntime.PatchEngine;

public interface IFrameworkResolver
{
    string FindFramework();
}

public class DefaultFrameworkResolver : IFrameworkResolver
{
    public string FindFramework()
    {
        var targetFramework = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
        var runtimeDirectory = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        var dotnetRoot = runtimeDirectory.Parent?.Parent?.Parent;
        var referencePacks = dotnetRoot is null
            ? null
            : Path.Combine(dotnetRoot.FullName, "packs", "Microsoft.NETCore.App.Ref");

        if (referencePacks is not null && Directory.Exists(referencePacks))
        {
            var installed = Directory.GetDirectories(referencePacks)
                .Select(path => (Path: path, Version: NuGetVersion.TryParse(Path.GetFileName(path), out var version) ? version : null))
                .Where(candidate => candidate.Version?.Major == Environment.Version.Major
                    && candidate.Version.Minor == Environment.Version.Minor)
                .OrderByDescending(candidate => candidate.Version)
                .Select(candidate => Path.Combine(candidate.Path, "ref", targetFramework))
                .FirstOrDefault(Directory.Exists);

            if (installed is not null)
                return installed;
        }

        var runtimeVersion = typeof(object).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0];
        var package = InstallPackage("Microsoft.NETCore.App.Ref", runtimeVersion, includePreReleases: true).SingleOrDefault();
        var downloaded = package is null
            ? null
            : Directory.GetDirectories(Path.Combine(package, "ref")).SingleOrDefault();

        return downloaded ?? throw new DirectoryNotFoundException(
            $"Could not locate the {targetFramework} Microsoft.NETCore.App reference pack. " +
            "Install the matching .NET SDK before running the patcher.");
    }

    public static IEnumerable<string> InstallPackage(string name, string? version = null, bool includePreReleases = false)
    {
        List<string> paths = new();
        var basedirectory = "dependencies";
        Directory.CreateDirectory(basedirectory);

        var task = Task.Run(async () => await ResolvePackageAsync(name, version, includePreReleases));
        task.Wait();
        var res = task.Result;
        if (res is not null)
            foreach (var package in res)
            {
                var zippath = Path.Combine("dependencies", package.PackageName + ".nupkg");
                var extractpath = Path.Combine("dependencies", package.PackageName);

                if (!File.Exists(zippath))
                {
                    if (package.Stream is not null)
                    {
                        package.Stream.Position = 0;
                        File.WriteAllBytes(zippath, package.Stream.ToArray());

                        try
                        {
                            if (Directory.Exists(extractpath)) Directory.Delete(extractpath, true);
                            ZipFile.ExtractToDirectory(zippath, extractpath);

                            paths.Add(extractpath);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(ex);
                        }
                    }
                }
                else
                {
                    paths.Add(extractpath);
                }
            }

        return paths;
    }

    public class PackageSource
    {
        public string PackageName { get; set; }
        public MemoryStream? Stream { get; set; }

        public PackageSource(string packageName, MemoryStream? stream)
        {
            PackageName = packageName;
            Stream = stream;
        }
    }
    static Dictionary<string, IEnumerable<PackageSource>?> _resolvePackageCache = new();
    static SourceCacheContext _nugetCache = new();
    public static async Task<IEnumerable<PackageSource>?> ResolvePackageAsync(string packageName, string? packageVersion = null, bool includePreReleases = false)
    {
        var key = packageName + (packageVersion ?? "latest");
        if (_resolvePackageCache.TryGetValue(key, out IEnumerable<PackageSource>? stream))
        {
            if (stream is not null)
                foreach (var srm in stream)
                    if (srm.Stream is not null)
                        srm.Stream.Position = 0;
            return stream;
        }

        List<PackageSource> streams = new();

        var repository = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");

        var resource = await repository.GetResourceAsync<FindPackageByIdResource>()
            ?? throw new InvalidOperationException("NuGet did not provide a package lookup resource.");

        var versions = (
            await resource.GetAllVersionsAsync(
                packageName,
                _nugetCache,
                NullLogger.Instance,
                CancellationToken.None
            )
        )
            .Where(x => !x.IsPrerelease || x.IsPrerelease == includePreReleases)
            .OrderByDescending(x => x.Version);

        NuGetVersion? version;
        packageVersion ??= Environment.Version.ToString();
        if (packageVersion is null)
            version = versions.FirstOrDefault();
        else version = versions.FindBestMatch(VersionRange.Parse(packageVersion), version => version);

        if (version is null)
        {
            _resolvePackageCache[key] = null;
            return null;
        }

        MemoryStream packageStream = new();
        await resource.CopyNupkgToStreamAsync(
            packageName,
            version,
            packageStream,
            _nugetCache,
            NullLogger.Instance,
            CancellationToken.None).ConfigureAwait(false);

        _resolvePackageCache[key] = streams;

        if (packageStream.Length > 0)
            streams.Add(new(packageName, packageStream));

        var dependencies = await resource.GetDependencyInfoAsync(packageName, version, _nugetCache,
            NullLogger.Instance,
            CancellationToken.None);

        var deps = dependencies.DependencyGroups.Where(x => x.TargetFramework.Framework == ".NETStandard");
        foreach (var dependency in deps)
        {
            foreach (var package in dependency.Packages)
            {
                if (_resolvePackageCache.TryGetValue(package.Id, out IEnumerable<PackageSource>? existing))
                    continue;

                MemoryStream depStream = new();
                await resource.CopyNupkgToStreamAsync(
                    package.Id,
                    package.VersionRange.MaxVersion ?? package.VersionRange.MinVersion,
                    depStream,
                    _nugetCache,
                    NullLogger.Instance,
                    CancellationToken.None);

                if (depStream.Length > 0)
                    streams.Add(new(package.Id, depStream));
            }
        }

        return streams;
    }

}
