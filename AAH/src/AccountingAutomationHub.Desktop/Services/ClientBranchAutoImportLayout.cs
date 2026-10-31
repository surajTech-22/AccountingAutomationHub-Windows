using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AccountingAutomationHub.Desktop.Services;

/// <summary>
/// Safe ownership resolution for ordinary branch Auto Import roots.
/// Named branches live directly beneath the existing XYZ client folder.
/// The old XYZ/Branches hierarchy is accepted only for safe migration.
/// Neither branch gets a duplicate Client Directory entry.
///
/// A folder marker identifies the branch; child ClientId/FY ownership and
/// source checks still gate every automatic accounting write.
/// </summary>
public static class ClientBranchAutoImportLayout
{
    public const string BranchesDirectory = "Branches";
    public const string Marker = ".aah-branch.json";
    private static readonly string[] Modules =
    [
        "Banking", "Purchase", "Sales", "Cash", "Journal", "GST",
        "Masters", "Fixed Assets", "Stock", "Other"
    ];

    public static IReadOnlyList<BranchAutoImportFolder> Ensure(
        string registeredClientFolder,
        long clientId,
        IReadOnlyList<ClientBranchRecord> branches)
    {
        if (clientId <= 0) throw new InvalidOperationException("Invalid client account owner.");
        if (branches.Count == 0) return []; // Original normal-client layout unchanged.
        var root = Path.GetFullPath(registeredClientFolder);
        if (!Directory.Exists(root)) return [];
        if (new DirectoryInfo(root).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Cannot create branch folders through a linked filesystem junction.");
        var legacyContainer = Path.Combine(root, BranchesDirectory);
        if (Directory.Exists(legacyContainer) &&
            new DirectoryInfo(legacyContainer).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Legacy branch container cannot be a filesystem junction.");
        // Do not create XYZ/Branches for new clients. Existing branch folders
        // are safely moved from that legacy container only when the direct
        // target is unoccupied and an exact saved branch marker matches.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BranchAutoImportFolder>();
        foreach (var branch in branches)
        {
            if (string.IsNullOrWhiteSpace(branch.BranchId) || !assigned.Add(branch.BranchId))
                throw new InvalidOperationException("Every branch must have a unique durable identity.");
            var existing = FindByMarker(root, clientId, branch.BranchId);
            var oldPath = Directory.Exists(legacyContainer)
                ? FindByMarker(legacyContainer, clientId, branch.BranchId) : null;
            var segment = existing is not null ? Path.GetFileName(existing)
                : oldPath is not null ? Path.GetFileName(oldPath) : SafeSegment(branch.Name);
            if (!seen.Add(segment))
                throw new InvalidOperationException("Two branch identities resolve to the same folder name.");
            var path = existing ?? Path.Combine(root, segment);
            if (existing is null && oldPath is not null)
            {
                // No overwrite, merge, delete or cross-volume copy. Preserve
                // files, marker, history and exact original folder name.
                if (Directory.Exists(path) || File.Exists(path))
                    throw new InvalidOperationException("Flat branch destination already exists: " + path);
                Directory.Move(oldPath, path);
            }
            if (existing is null && oldPath is null)
            {
                // Never claim a user-created folder silently. It might contain
                // data for another company, and a recursive watcher is live.
                if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                    throw new InvalidOperationException("Unowned branch directory already contains files: " + path);
                Directory.CreateDirectory(path);
                var metadata = JsonSerializer.Serialize(new BranchFolderMarker(clientId, branch.BranchId, branch.Gstin));
                File.WriteAllText(Path.Combine(path, Marker), metadata, Encoding.UTF8);
            }
            if (new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Branch folder is a filesystem junction: " + path);
            // A named branch is another NORMAL linked Auto Import root:
            // drop Sales, Purchase, Bank and other supported files directly
            // here; the existing classifier decides their module. Never
            // manufacture an entire second tree of module folders per branch.
            // v537 created these folders; remove ONLY proven empty directories.
            // User content in any former module folder stays untouched.
            CleanupEmptyLegacyModuleDirectories(path, branch.Gstin);
            // The legacy container may still hold unrelated user data;
            // only remove it when it is proven completely empty.
            if (Directory.Exists(legacyContainer) &&
                !Directory.EnumerateFileSystemEntries(legacyContainer).Any())
                Directory.Delete(legacyContainer, recursive: false);
            result.Add(new BranchAutoImportFolder(
                clientId, branch.BranchId, branch.Name, branch.Gstin, path));
        }
        return result;
    }

    /// <summary>
    /// Recognize child folders by a verified marker rather than a filename,
    /// GSTIN regex, nearby folder, or previous client's matching branch name.
    /// Returns false for the normal client folder, output folders and escapes.
    /// </summary>
    public static bool TryResolve(string registeredClientFolder, string filePath,
        out BranchAutoImportFolder? match)
    {
        match = null;
        try
        {
            var root = Path.GetFullPath(registeredClientFolder);
            var candidate = Path.GetFullPath(filePath);
            // Direct branch path: XYZ/Karnataka/source.xlsx
            if (TryResolveUnder(root, candidate, out match)) return true;
            // Backward compatibility for v536-v538 source files not yet moved.
            return TryResolveUnder(Path.Combine(root, BranchesDirectory), candidate, out match);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    public static bool IsWithinBranchPath(string registeredClientFolder, string filePath)
    {
        try
        {
            var root = Path.GetFullPath(registeredClientFolder);
            var candidate = Path.GetFullPath(filePath);
            // Fail closed for any historical XYZ/Branches path, including
            // an unmarked folder: never route it to parent accounting.
            var legacy = Path.GetFullPath(Path.Combine(root, BranchesDirectory))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (candidate.StartsWith(legacy, StringComparison.OrdinalIgnoreCase)) return true;
            return TryResolveUnder(root, candidate, out _);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    private static bool TryResolveUnder(string container, string candidate, out BranchAutoImportFolder? match)
    {
        match = null;
        if (!Directory.Exists(container) ||
            new DirectoryInfo(container).Attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;
        var prefix = Path.GetFullPath(container)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var relative = candidate[prefix.Length..];
        var segment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(segment) ||
            relative.Length <= segment.Length) return false;
        var directory = Path.Combine(container, segment);
        if (!Directory.Exists(directory) ||
            new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;
        if (!TryReadMarker(Path.Combine(directory, Marker), out var marker)) return false;
        match = new BranchAutoImportFolder(
            marker!.ClientId, marker.BranchId, segment, marker.Gstin, directory);
        return true;
    }

    private static void CleanupEmptyLegacyModuleDirectories(string branchFolder, string gstin)
    {
        // These exact names were generated by v536/v537. Do not delete files,
        // remove populated directories, follow junctions, or touch unknown
        // user-created names. This is idempotent across restarts.
        var gstFolder = Path.Combine(branchFolder, "GST");
        if (gstin.Length == 15)
        {
            var gstinFolder = Path.Combine(gstFolder, gstin);
            foreach (var name in new[] { "GSTR-3B", "Electronic Credit Ledger", "Electronic Cash Ledger" })
                DeleteDirectoryIfEmpty(Path.Combine(gstinFolder, name));
            DeleteDirectoryIfEmpty(gstinFolder);
        }
        foreach (var module in Modules)
            DeleteDirectoryIfEmpty(Path.Combine(branchFolder, module));
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            var info = new DirectoryInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                Directory.EnumerateFileSystemEntries(path).Any()) return;
            Directory.Delete(path, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never fail the Auto Import watcher when an old, empty folder is
            // locked. It can be considered again on the next safe scan.
        }
    }

    private static string? FindByMarker(string container, long clientId, string branchId)
    {
        foreach (var directory in Directory.EnumerateDirectories(container))
        {
            if (new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            if (!TryReadMarker(Path.Combine(directory, Marker), out var marker)) continue;
            if (marker!.ClientId == clientId &&
                marker.BranchId.Equals(branchId, StringComparison.OrdinalIgnoreCase))
                return directory; // Rename does not move already imported source files.
        }
        return null;
    }

    private static bool TryReadMarker(string path, out BranchFolderMarker? marker)
    {
        marker = null;
        if (!File.Exists(path)) return false;
        try
        {
            var value = JsonSerializer.Deserialize<BranchFolderMarker>(File.ReadAllText(path));
            if (value is null || value.ClientId <= 0 ||
                value.BranchId.Length == 0 || value.BranchId.Length > 80)
                return false;
            marker = value;
            return true;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        { return false; }
    }

    public static string SafeSegment(string? name)
    {
        var trimmed = (name ?? "").Trim().TrimEnd('.', ' ');
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()
            .Concat("<>:\"/\\|?*".ToCharArray()));
        var cleaned = new string(trimmed.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.', ' ');
        if (cleaned.Length > 70) cleaned = cleaned[..70].TrimEnd('.', ' ');
        if (cleaned.Length == 0) throw new InvalidOperationException("Branch name cannot become an empty folder name.");
        var basename = cleaned.Split('.')[0];
        if (new[] {"CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4",
                   "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2",
                   "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"}
            .Contains(basename, StringComparer.OrdinalIgnoreCase))
            cleaned = "_" + cleaned;
        return cleaned;
    }

    private sealed record BranchFolderMarker(long ClientId, string BranchId, string Gstin);
}
public sealed record BranchAutoImportFolder(
    long ClientId, string BranchId, string Name, string Gstin, string FolderPath);
