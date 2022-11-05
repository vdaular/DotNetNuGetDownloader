﻿using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Packaging.PackageExtraction;
using NuGet.Packaging;
using NuGet.Packaging.Signing;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace DotNetNuGetDownloader;

public class DotNetNuGetPackageDownloader
{
    private readonly ILogger _logger;

    public DotNetNuGetPackageDownloader(ILogger logger = null)
    {
        _logger = logger ?? new ConsoleLogger();
    }

    public async Task<NuGetDownloadResult> DownloadAsync(
        string packageFolder,
        string packageName,
        string packageVersion = null,
        bool includePrerelease = false,
        NuGetFeed packageFeed = null,
        bool onlyDownload = false,
        bool includeSecondaryRepositories = false,
        string targetFramework = null,
        string targetRid = null,
        bool filterOurRefFiles = true
    )
    {
        return await DownloadAsync(
            packageFolder,
            packageName,
            packageVersion,
            includePrerelease,
            packageFeed,
            onlyDownload,
            includeSecondaryRepositories,
            targetFramework,
            targetRid,
            filterOurRefFiles,
            null
        );
    }

    public async Task<string[]> DownloadAsync(
        IPackageSearchMetadata packageIdentity,
        SourceRepository repository,
        string downloadFolder, bool onlyDownload = false
        )
    {
        if (packageIdentity == null)
            throw new ArgumentNullException(nameof(packageIdentity));

        var providers = GetNuGetResourceProviders();
        var settings = Settings.LoadDefaultSettings(downloadFolder, null, new MachineWideSettings());
        var packageSourceProvider = new PackageSourceProvider(settings);
        var sourceRepositoryProvider = new SourceRepositoryProvider(packageSourceProvider, providers);
        var dotNetFramework = Assembly.GetEntryAssembly().GetCustomAttribute<TargetFrameworkAttribute>().FrameworkName;

        var frameworkNameProvider = new FrameworkNameProvider(
            new[] { DefaultFrameworkMappings.Instance },
            new[] { DefaultPortableFrameworkMappings.Instance }
        );

        var nuGetFramework = NuGetFramework.ParseFrameworkName(dotNetFramework, frameworkNameProvider);
        var project = new ModuleFolderNuGetProject(downloadFolder, packageIdentity, nuGetFramework, onlyDownload);
        var packageManager = new NuGetPackageManager(sourceRepositoryProvider, settings, downloadFolder) { PackagesFolderNuGetProject = project };
        var clientPolicyContext = ClientPolicyContext.GetClientPolicy(settings, _logger);

        var projectContext = new FolderProjectContext(_logger)
        {
            PackageExtractionContext = new PackageExtractionContext(
                PackageSaveMode.Defaultv2,
                PackageExtractionBehavior.XmlDocFileSaveMode,
                clientPolicyContext,
                _logger
            )
        };

        var resolutionContext = new ResolutionContext(
            DependencyBehavior.Lowest,
            true,
            includeUnlisted: false,
            VersionConstraints.None
        );

        var downloadContext = new PackageDownloadContext(
            resolutionContext.SourceCacheContext,
            downloadFolder,
            resolutionContext.SourceCacheContext.DirectDownload
        );

        await packageManager.InstallPackageAsync(
            project,
            packageIdentity.Identity,
            resolutionContext,
            projectContext,
            downloadContext,
            repository,
            new List<SourceRepository>(),
            CancellationToken.None
        );

        var versionFolder = Path.Combine(downloadFolder, packageIdentity.ToString());

        return Directory.GetFiles(versionFolder, "*.*", SearchOption.AllDirectories);
    }

    public async Task<NuGetDownloadResult> DownloadAsync(
        string packageFolder,
        string packageName,
        string packageVersion = null,
        bool includePrerelease = false,
        NuGetFeed packageFeed = null,
        bool onlyDownload = false,
        bool includeSecondaryRepositories = false,
        string targetFramework = null,
        string targetRid = null,
        bool filterOurRefFiles = true,
        List<string> ignoredSources = null
    )
    {
        return await DownloadAsync(
            packageFolder,
            packageName,
            packageVersion,
            includePrerelease,
            packageFeed,
            onlyDownload,
            includeSecondaryRepositories,
            targetFramework,
            targetRid,
            filterOurRefFiles,
            null,
            false
        );
    }

    public async Task<NuGetDownloadResult> DownloadAsync(
        string packageFolder,
        string packageName,
        string packageVersion = null,
        bool includePrerelease = false,
        NuGetFeed packageFeed = null,
        bool onlyDownload = false,
        bool includeSecondaryRepositories = false,
        string targetFramework = null,
        string targetRid = null,
        bool filterOurRefFiles = true,
        List<string> ignoredSources = null,
        bool autoRetryOnFail = false
    )
    {
        if (!Directory.Exists(packageFolder))
            Directory.CreateDirectory(packageFolder);

        var providers = GetNuGetResourceProviders();
        var settings = Settings.LoadDefaultSettings(packageFolder, null, new MachineWideSettings());
        var packageSourceProvider = new PackageSourceProvider(settings);
        var sourceRepositoryProvider = new SourceRepositoryProvider(packageSourceProvider, providers);

        IPackageSearchMetadata package = null;
        SourceRepository sourceRepo = null;

        var packageResult = await GetPackage(packageName, packageVersion, includePrerelease, packageFeed, providers, sourceRepositoryProvider, false);

        package = packageResult.Item2;
        sourceRepo = packageResult.Item1;

        if (package == null)
        {
            if (autoRetryOnFail)
            {
                packageResult = await GetPackage(packageName, packageVersion, includePrerelease, packageFeed, providers, sourceRepositoryProvider, true);

                package = packageResult.Item2;
                sourceRepo = packageResult.Item1;
            }

            if (package == null)
                throw new PackageNotFoundException($"Couldn't find package '{packageName}'.{packageVersion}.");
        }

        if (string.IsNullOrWhiteSpace(targetFramework))
            targetFramework = Assembly.GetEntryAssembly().GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

        var frameworkNameProvider = new FrameworkNameProvider(
            new[] { DefaultFrameworkMappings.Instance },
            new[] { DefaultPortableFrameworkMappings.Instance }
        );

        var nuGetFramework = NuGetFramework.ParseFrameworkName(targetFramework, frameworkNameProvider);
        var project = new ModuleFolderNuGetProject(packageFolder, package, nuGetFramework, onlyDownload, targetRid, filterOurRefFiles);
        var packageManager = new NuGetPackageManager(sourceRepositoryProvider, settings, packageFolder) { PackagesFolderNuGetProject = project };
        var clientPolicyContext = ClientPolicyContext.GetClientPolicy(settings, _logger);

        var projectContext = new FolderProjectContext(_logger)
        {
            PackageExtractionContext = new PackageExtractionContext(
                PackageSaveMode.Defaultv3,
                PackageExtractionBehavior.XmlDocFileSaveMode,
                clientPolicyContext,
                _logger
            )
        };

        var resolutionContext = new ResolutionContext(
            DependencyBehavior.Lowest,
            includePrerelease,
            includeUnlisted: false,
            VersionConstraints.None
        );

        var downloadContext = new PackageDownloadContext(
            resolutionContext.SourceCacheContext,
            packageFolder,
            resolutionContext.SourceCacheContext.DirectDownload
        );

        var secondaryRepos = sourceRepositoryProvider.GetRepositories();

        if (!includeSecondaryRepositories)
            secondaryRepos = new List<SourceRepository>();

        if ((bool)(ignoredSources?.Any()))
            secondaryRepos = secondaryRepos.Where(x => ignoredSources.Contains(x.PackageSource.Name) == false).ToList();

        await packageManager.InstallPackageAsync(
            project,
            package.Identity,
            resolutionContext,
            projectContext,
            downloadContext,
            sourceRepo,
            secondaryRepos,
            CancellationToken.None
        );

        await project.PostProcessAsync(projectContext, CancellationToken.None);
        await project.PreProcessAsync(projectContext, CancellationToken.None);
        await packageManager.RestorePackageAsync(package.Identity, projectContext, downloadContext, new[] { sourceRepo }, CancellationToken.None);

        var result = new NuGetDownloadResult
        {
            Context = new NuGetContext(
                nuGetFramework.ToString(),
                nuGetFramework.GetShortFolderName(),
                nuGetFramework.Version.ToString(),
                packageFolder,
                packageName,
                packageVersion,
                project.Rid,
                project.SupportedRids
            )
        };

        if (onlyDownload)
        {
            var versionFolder = Path.Combine(packageFolder, package.Identity.ToString());

            result.PackageAssemblyFiles = new List<string>(Directory.GetFiles(versionFolder, "*.*", SearchOption.AllDirectories));

            return result;
        }

        result.InstalledDlls = new List<DllInfo>(project.InstalledDlls);
        result.RuntimeDlls = new List<RuntimeDll>(project.RuntimeDlls);
        result.InstalledPackages = new List<string>(project.InstalledPackages);

        return result;
    }

    private async Task<(SourceRepository, IPackageSearchMetadata)> GetPackage(
        string packageName,
        string packageVersion,
        bool includePrerelease,
        NuGetFeed packageFeed,
        List<Lazy<INuGetResourceProvider>> providers,
        SourceRepositoryProvider sourceRepositoryProvider,
        bool forceRefresh
    )
    {
        IPackageSearchMetadata package = null;
        SourceRepository sourceRepo = null;

        if (!string.IsNullOrWhiteSpace(packageFeed?.Feed))
        {
            sourceRepo = GetSourceRepo(packageFeed, providers);

            package = await SearchPackageAsync(packageName, packageVersion, includePrerelease, sourceRepo, forceRefresh);
        }
        else
        {
            foreach (var repo in sourceRepositoryProvider.GetRepositories())
            {
                if (packageFeed != null && repo.PackageSource.Name != packageFeed.Name)
                    continue;

                package = await SearchPackageAsync(packageName, packageVersion, includePrerelease, repo, forceRefresh);

                if (package != null)
                {
                    sourceRepo = repo;

                    break;
                }
            }
        }

        return (sourceRepo, package);
    }

    private static SourceRepository GetSourceRepo(NuGetFeed packageFeed, List<Lazy<INuGetResourceProvider>> providers)
    {
        SourceRepository sourceRepo;
        var packageSource = new PackageSource(packageFeed.Feed);

        if (!string.IsNullOrWhiteSpace(packageFeed.Username))
            packageSource.Credentials = new PackageSourceCredential(packageFeed.Name, packageFeed.Username, packageFeed.Password, true, null);

        sourceRepo = new SourceRepository(packageSource, providers);

        return sourceRepo;
    }

    public async IAsyncEnumerable<(SourceRepository Repository, IPackageSearchMetadata Package)> SearchPackageAsync(
        string searchTerm,
        int maxResults = 128,
        bool includePrerelease = false,
        string nugetConfigFilePath = ""
    )
    {
        var providers = GetNuGetResourceProviders();
        var packageRootFolder = "";

        if (!string.IsNullOrWhiteSpace(Assembly.GetEntryAssembly()?.Location))
            packageRootFolder = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location);

        ISettings settings;

        if (!string.IsNullOrWhiteSpace(nugetConfigFilePath))
            settings = Settings.LoadSettingsGivenConfigPaths(new List<string>() { nugetConfigFilePath });
        else
            settings = Settings.LoadDefaultSettings(packageRootFolder ?? "", null, new MachineWideSettings());

        var packageSourceProvider = new PackageSourceProvider(settings);
        var sourceRepositoryProvider = new SourceRepositoryProvider(packageSourceProvider, providers);
        var repositories = sourceRepositoryProvider.GetRepositories();

        foreach (var repository in repositories)
        {
            var packageSearchResource = await repository.GetResourceAsync<PackageSearchResource>();

            SearchFilter searchFilter;

            if (includePrerelease)
                searchFilter = new SearchFilter(includePrerelease, SearchFilterType.IsAbsoluteLatestVersion);
            else
                searchFilter = new SearchFilter(includePrerelease);

            var items = await packageSearchResource.SearchAsync(searchTerm, searchFilter, 0, maxResults, _logger, CancellationToken.None);

            foreach (var packageSearchMetadata in items)
                yield return (repository, packageSearchMetadata);
        }
    }

    public async Task<IEnumerable<(SourceRepository Repository, IPackageSearchMetadata Package)>> SearchPackagesAsync(
        NuGetFeed packageFeed,
        string searchTerm,
        int maxResults = 128,
        bool includePrerelease = false
    )
    {
        var providers = GetNuGetResourceProviders();
        var sourceRepo = GetSourceRepo(packageFeed, providers);
        var packageSearchResource = await sourceRepo.GetResourceAsync<PackageSearchResource>();

        SearchFilter searchFilter;

        if (includePrerelease)
            searchFilter = new SearchFilter(includePrerelease, SearchFilterType.IsAbsoluteLatestVersion);
        else
            searchFilter = new SearchFilter(includePrerelease);

        var packages = await packageSearchResource.SearchAsync(searchTerm, searchFilter, 0, maxResults, _logger, CancellationToken.None);

        return packages.Select(x => (sourceRepo, x));
    }

    private async Task<IPackageSearchMetadata> SearchPackageAsync(
        string packageName,
        string version,
        bool includePrerelease,
        SourceRepository sourceRepository,
        bool forceRefresh
    )
    {
        var packageMetadataResource = await sourceRepository.GetResourceAsync<PackageMetadataResource>();
        var sourceCacheContext = new SourceCacheContext();

        if (forceRefresh)
            sourceCacheContext = sourceCacheContext.WithRefreshCacheTrue();

        IPackageSearchMetadata packageMetadata = null;

        if (!string.IsNullOrEmpty(version) && !version.Contains('*'))
        {
            if (NuGetVersion.TryParse(version, out var nugetversion))
            {
                var packageIdentity = new PackageIdentity(packageName, NuGetVersion.Parse(version));

                packageMetadata = await packageMetadataResource.GetMetadataAsync(
                    packageIdentity,
                    sourceCacheContext,
                    _logger,
                    CancellationToken.None
                );
            }
        }
        else
        {
            var searchResults = await packageMetadataResource.GetMetadataAsync(
                packageName,
                includePrerelease,
                includeUnlisted: false,
                sourceCacheContext,
                _logger,
                CancellationToken.None
            );

            searchResults = searchResults.OrderByDescending(p => p.Identity.Version);

            if (!string.IsNullOrEmpty(version))
            {
                var searchPattern = version.Replace("*", ".*");
                searchResults = searchResults.Where(p => Regex.IsMatch(p.Identity.Version.ToString(), searchPattern));
            }

            packageMetadata = searchResults.FirstOrDefault();
        }

        return packageMetadata;
    }

    private static List<Lazy<INuGetResourceProvider>> GetNuGetResourceProviders()
    {
        var providers = new List<Lazy<INuGetResourceProvider>>();
        providers.AddRange(Repository.Provider.GetCoreV3());

        return providers;
    }
}