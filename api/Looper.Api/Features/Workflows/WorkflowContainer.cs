using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Looper.Api.Features.Workflows;

public sealed class WorkflowContainerException(string message) : Exception(message);

/// <summary>
/// The on-disk form of a workflow package: a <c>.workflow</c> file. It is a zip container with a
/// fixed layout, so any archive tool can open it, while the manifest makes it self-describing and
/// tamper-evident:
/// <code>
///   manifest.json                 format, versions, workflow name, SHA-256 of every other entry
///   workflow.json                 the package (resources, agents, wiring, redacted secrets)
///   types/&lt;TypeKey&gt;/module.cs    source of each dynamic resource type
///   types/&lt;TypeKey&gt;/module.dll   its compiled module, raw bytes
/// </code>
/// Unpacking fails closed: a wrong format, a missing entry, an unlisted entry or a checksum that
/// does not match is refused before anything is created.
/// </summary>
public static class WorkflowContainer
{
    public const string Extension = ".workflow";
    public const string MediaType = "application/vnd.looper.workflow+zip";
    public const int ContainerVersion = 1;
    private const string ManifestEntry = "manifest.json";
    private const string WorkflowEntry = "workflow.json";
    private const long MaxEntryBytes = 32L * 1024 * 1024;

    public sealed record Manifest(
        string Format,
        int Container,
        int Version,
        DateTime ExportedAtUtc,
        string? ExportedFrom,
        PackagedWorkflow Workflow,
        /// <summary>Entry name → "sha256:&lt;hex&gt;" for every entry except the manifest itself.</summary>
        Dictionary<string, string> Entries);

    public static string FileNameFor(string workflowName) =>
        Path.ChangeExtension(WorkflowPackage.FileNameFor(workflowName), null).Replace(".looper-workflow", "") + Extension;

    public static byte[] Pack(WorkflowPackage package)
    {
        var entries = new List<(string Name, byte[] Bytes)>();

        // The workflow document carries everything except module code, which lives in its own files.
        var stripped = package with
        {
            ResourceTypes = package.ResourceTypes
                .Select(t => t with { SourceCode = "", DllBase64 = null })
                .ToList()
        };
        entries.Add((WorkflowEntry, JsonSerializer.SerializeToUtf8Bytes(stripped, WorkflowPackage.JsonOptions)));
        foreach (var type in package.ResourceTypes.OrderBy(t => t.TypeKey, StringComparer.Ordinal))
        {
            entries.Add(($"types/{type.TypeKey}/module.cs", Encoding.UTF8.GetBytes(type.SourceCode)));
            if (!string.IsNullOrEmpty(type.DllBase64))
            {
                entries.Add(($"types/{type.TypeKey}/module.dll", Convert.FromBase64String(type.DllBase64)));
            }
        }

        var manifest = new Manifest(
            WorkflowPackage.FormatName, ContainerVersion, package.Version, package.ExportedAtUtc, package.ExportedFrom, package.Workflow,
            entries.ToDictionary(e => e.Name, e => Checksum(e.Bytes), StringComparer.Ordinal));

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest, WorkflowPackage.JsonOptions));
            foreach (var (name, bytes) in entries) Write(zip, name, bytes);
        }
        return stream.ToArray();
    }

    public static WorkflowPackage Unpack(byte[] bytes)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new WorkflowContainerException($"This is not a Looper {Extension} package (not a valid container).");
        }

        using (zip)
        {
            var manifestEntry = zip.GetEntry(ManifestEntry)
                ?? throw new WorkflowContainerException($"This is not a Looper {Extension} package (no manifest).");
            Manifest manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<Manifest>(Read(manifestEntry), WorkflowPackage.JsonOptions)
                    ?? throw new WorkflowContainerException("The package manifest is empty.");
            }
            catch (JsonException ex)
            {
                throw new WorkflowContainerException($"The package manifest is not readable: {ex.Message}");
            }

            if (manifest.Format != WorkflowPackage.FormatName)
            {
                throw new WorkflowContainerException($"This is not a Looper workflow package (format '{manifest.Format}').");
            }
            if (manifest.Container > ContainerVersion)
            {
                throw new WorkflowContainerException(
                    $"This package was written by a newer Looper (container version {manifest.Container}, this one reads up to {ContainerVersion}). Update Looper.");
            }
            if (manifest.Entries is null || !manifest.Entries.ContainsKey(WorkflowEntry))
            {
                throw new WorkflowContainerException("The package manifest does not list a workflow document.");
            }

            // Every listed entry must be present and intact; nothing unlisted may ride along.
            var contents = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (name, expected) in manifest.Entries)
            {
                var entry = zip.GetEntry(name) ?? throw new WorkflowContainerException($"The package is missing '{name}'.");
                var data = Read(entry);
                if (!string.Equals(Checksum(data), expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new WorkflowContainerException($"The package is corrupt or was modified: '{name}' does not match its checksum.");
                }
                contents[name] = data;
            }
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName != ManifestEntry && !contents.ContainsKey(entry.FullName))
                {
                    throw new WorkflowContainerException($"The package contains an entry the manifest does not list: '{entry.FullName}'.");
                }
            }

            WorkflowPackage package;
            try
            {
                package = JsonSerializer.Deserialize<WorkflowPackage>(contents[WorkflowEntry], WorkflowPackage.JsonOptions)
                    ?? throw new WorkflowContainerException("The workflow document is empty.");
            }
            catch (JsonException ex)
            {
                throw new WorkflowContainerException($"The workflow document is not readable: {ex.Message}");
            }

            var types = new List<PackagedResourceType>();
            foreach (var type in package.ResourceTypes ?? [])
            {
                if (!contents.TryGetValue($"types/{type.TypeKey}/module.cs", out var source))
                {
                    throw new WorkflowContainerException($"The package lists the resource type '{type.TypeKey}' but carries no source for it.");
                }
                contents.TryGetValue($"types/{type.TypeKey}/module.dll", out var dll);
                types.Add(type with { SourceCode = Encoding.UTF8.GetString(source), DllBase64 = dll is null ? null : Convert.ToBase64String(dll) });
            }

            return package with
            {
                Format = manifest.Format,
                Version = package.Version == 0 ? manifest.Version : package.Version,
                ExportedAtUtc = manifest.ExportedAtUtc,
                ExportedFrom = manifest.ExportedFrom,
                Workflow = package.Workflow ?? manifest.Workflow,
                ResourceTypes = types
            };
        }
    }

    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); // stable bytes for identical content
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes)
        {
            throw new WorkflowContainerException($"'{entry.FullName}' is larger than a workflow package entry can be.");
        }
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string Checksum(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
