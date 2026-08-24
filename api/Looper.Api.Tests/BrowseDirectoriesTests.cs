using Looper.Api.Features.FileSystem;

namespace Looper.Api.Tests;

public sealed class BrowseDirectoriesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"looper-browse-{Guid.NewGuid():N}");
    private readonly BrowseDirectoriesHandler _handler = new();

    public BrowseDirectoriesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        Directory.CreateDirectory(Path.Combine(_root, "beta"));
        Directory.CreateDirectory(Path.Combine(_root, ".hidden"));
        File.WriteAllText(Path.Combine(_root, "not-a-folder.txt"), "x");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Lists_only_directories_sorted_and_flags_hidden_ones()
    {
        var listing = await _handler.Handle(new BrowseDirectoriesQuery(_root), default);

        Assert.True(listing.Exists);
        Assert.Null(listing.Error);
        Assert.Equal([".hidden", "alpha", "beta"], listing.Directories.Select(d => d.Name));
        Assert.True(listing.Directories.Single(d => d.Name == ".hidden").IsHidden);
        Assert.False(listing.Directories.Single(d => d.Name == "alpha").IsHidden);
        Assert.All(listing.Directories, d => Assert.True(Path.IsPathRooted(d.Path)));
    }

    [Fact]
    public async Task Exposes_the_parent_for_navigating_up()
    {
        var listing = await _handler.Handle(new BrowseDirectoriesQuery(Path.Combine(_root, "alpha")), default);

        Assert.Equal(_root, listing.ParentPath);
    }

    [Fact]
    public async Task Defaults_to_the_home_folder_and_offers_quick_links()
    {
        var listing = await _handler.Handle(new BrowseDirectoriesQuery(null), default);

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), listing.Path);
        Assert.True(listing.Exists);
        Assert.Contains(listing.QuickLinks, link => link.Label == "Home");
    }

    [Fact]
    public async Task Expands_a_tilde_path()
    {
        var listing = await _handler.Handle(new BrowseDirectoriesQuery("~"), default);

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), listing.Path);
    }

    [Fact]
    public async Task Normalizes_relative_segments_and_trailing_separators()
    {
        var messy = Path.Combine(_root, "alpha", "..", "beta") + Path.DirectorySeparatorChar;

        var listing = await _handler.Handle(new BrowseDirectoriesQuery(messy), default);

        Assert.Equal(Path.Combine(_root, "beta"), listing.Path);
        Assert.True(listing.Exists);
    }

    [Fact]
    public async Task Missing_folder_reports_not_existing_instead_of_throwing()
    {
        var listing = await _handler.Handle(
            new BrowseDirectoriesQuery(Path.Combine(_root, "nope", "still-nope")), default);

        Assert.False(listing.Exists);
        Assert.Empty(listing.Directories);
        Assert.NotEmpty(listing.QuickLinks); // the UI can still offer a way out
    }
}
