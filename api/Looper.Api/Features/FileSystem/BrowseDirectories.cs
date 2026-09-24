using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;

namespace Looper.Api.Features.FileSystem;

public sealed record DirectoryEntryDto(string Name, string Path, bool IsHidden);

public sealed record FileEntryDto(string Name, string Path, bool IsHidden, long SizeBytes);

public sealed record QuickLinkDto(string Label, string Path);

public sealed record DirectoryListingDto(
    string Path,
    string? ParentPath,
    bool Exists,
    string? Error,
    IReadOnlyList<DirectoryEntryDto> Directories,
    IReadOnlyList<QuickLinkDto> QuickLinks,
    /// <summary>Files in the folder — only when asked for (the file picker); empty for the folder picker.</summary>
    IReadOnlyList<FileEntryDto> Files);

/// <summary>
/// Lists sub-directories so the UI can offer a real folder picker — and, when asked, the files too,
/// so the same browser can pick a single file. Names and sizes only, never contents; the API is
/// bound to localhost, the same trust boundary as the agents it hands these paths to. A path that
/// names a file lists that file's folder, so a typed file path lands where the picker can select it.
/// </summary>
public sealed record BrowseDirectoriesQuery(string? Path, bool IncludeFiles = false) : IQuery<DirectoryListingDto>;

public sealed class BrowseDirectoriesHandler : IQueryHandler<BrowseDirectoriesQuery, DirectoryListingDto>
{
    public Task<DirectoryListingDto> Handle(BrowseDirectoriesQuery query, CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var quickLinks = BuildQuickLinks(home);

        var path = Resolve(query.Path, home);
        if (query.IncludeFiles && File.Exists(path))
        {
            path = Path.GetDirectoryName(path) ?? path;
        }

        if (!Directory.Exists(path))
        {
            return Task.FromResult(new DirectoryListingDto(
                path, Parent(path), Exists: false, Error: null, [], quickLinks, []));
        }

        try
        {
            var directories = Directory.EnumerateDirectories(path)
                .Select(dir => new DirectoryEntryDto(Path.GetFileName(dir), dir, Path.GetFileName(dir).StartsWith('.')))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var files = query.IncludeFiles
                ? Directory.EnumerateFiles(path)
                    .Select(file => new FileEntryDto(Path.GetFileName(file), file, Path.GetFileName(file).StartsWith('.'), SizeOf(file)))
                    .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];

            return Task.FromResult(new DirectoryListingDto(
                path, Parent(path), Exists: true, Error: null, directories, quickLinks, files));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Task.FromResult(new DirectoryListingDto(
                path, Parent(path), Exists: true, Error: "This folder can’t be opened: " + ex.Message, [], quickLinks, []));
        }
    }

    /// <summary>Expands ~, makes the path absolute and strips a trailing separator.</summary>
    private static string Resolve(string? requested, string home)
    {
        if (string.IsNullOrWhiteSpace(requested)) return home;

        var path = requested.Trim();
        if (path == "~") return home;
        if (path.StartsWith("~/", StringComparison.Ordinal)) path = Path.Combine(home, path[2..]);

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return home;
        }
    }

    /// <summary>A file that vanishes between enumeration and stat must not fail the whole listing.</summary>
    private static long SizeOf(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static string? Parent(string path) => Directory.GetParent(path)?.FullName;

    private static List<QuickLinkDto> BuildQuickLinks(string home)
    {
        var links = new List<QuickLinkDto> { new("Home", home) };

        foreach (var folder in new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path) && path != home && Directory.Exists(path))
            {
                links.Add(new QuickLinkDto(Path.GetFileName(path), path));
            }
        }

        var root = Path.GetPathRoot(home);
        if (!string.IsNullOrEmpty(root)) links.Add(new QuickLinkDto(root, root));

        return links;
    }
}

public sealed class BrowseDirectoriesEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/filesystem/directories", (string? path, bool? includeFiles, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new BrowseDirectoriesQuery(path, includeFiles ?? false), ct));
}
