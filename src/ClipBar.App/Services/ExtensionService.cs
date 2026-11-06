using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ClipBar.Core.Extensions;
using ClipBar.Core.Models;
using ClipBar.Core.Security;

namespace ClipBar.App.Services;

/// <summary>
/// Owns the SnipDo extension store: importing .pbar packages, unpacking them into
/// the data folder, and enumerating the extensions available to the panel.
/// </summary>
public sealed class ExtensionService
{
    private const string ManifestHint = "extension.json";
    private const string InstalledManifestHint = "data.json";
    private readonly string _extensionsDirectory;
    private readonly Action<string> _log;
    private List<LoadedExtension> _extensions = [];
    private bool _loaded;

    public ExtensionService(string extensionsDirectory, Action<string>? log = null)
    {
        _extensionsDirectory = extensionsDirectory;
        _log = log ?? (_ => { });
    }

    public string ExtensionsDirectory => _extensionsDirectory;

    public IReadOnlyList<LoadedExtension> Extensions => _extensions;

    public void Reload()
    {
        var result = new List<LoadedExtension>();
        if (Directory.Exists(_extensionsDirectory))
        {
            foreach (var folder in Directory.EnumerateDirectories(_extensionsDirectory))
            {
                // ".import-*" / ".backup-*" are mid-import state (staging and swap backups).
                // Loading them used to expose half-copied packages as installed extensions.
                if (Path.GetFileName(folder).StartsWith('.'))
                {
                    continue;
                }

                var loaded = TryLoadFolder(folder);
                if (loaded is not null)
                {
                    result.Add(loaded);
                }
            }
        }

        _extensions = result;
        _loaded = true;
    }

    public void EnsureLoaded()
    {
        if (!_loaded)
        {
            Reload();
        }
    }

    /// <summary>
    /// Imports a .pbar (or .zip) package. The folder is named after the manifest
    /// identifier so re-importing the same extension replaces it.
    /// </summary>
    public async Task<LoadedExtension> ImportAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packagePath))
        {
            throw new SnipDoManifestException("文件不存在：" + packagePath);
        }

        Directory.CreateDirectory(_extensionsDirectory);
        var staging = Path.Combine(_extensionsDirectory, ".import-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await Task.Run(() => Extract(packagePath, staging), cancellationToken).ConfigureAwait(false);

            var manifestPath = FindManifest(staging);
            if (manifestPath is null)
            {
                throw new SnipDoManifestException("压缩包里没有 .json 扩展清单。");
            }

            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            var manifestRoot = Path.GetDirectoryName(manifestPath)!;
            var extension = SnipDoManifestParser.Parse(json, manifestRoot);

            var target = ResolveTargetDirectory(extension);
            AssertTargetIdentity(target, extension.Identifier);

            // F-02: swap the new package in through a backup instead of deleting the old
            // folder first. A failure mid-swap (locked file, denied ACL, cancelled token)
            // restores the old package rather than leaving it deleted.
            var backup = Path.Combine(_extensionsDirectory, ".backup-" + Guid.NewGuid().ToString("N")[..8]);
            var hasExisting = Directory.Exists(target);
            if (hasExisting)
            {
                Directory.Move(target, backup);
            }

            try
            {
                Directory.Move(manifestRoot, target);
            }
            catch
            {
                if (hasExisting && !Directory.Exists(target))
                {
                    Directory.Move(backup, target);
                }

                throw;
            }

            if (Directory.Exists(staging))
            {
                // Cleanup only: a locked leftover must not fail an import that already
                // swapped in cleanly. Reload skips dot-folders either way.
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            if (hasExisting && Directory.Exists(backup))
            {
                try
                {
                    SafePath.DeleteInside(_extensionsDirectory, backup);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            Reload();
            var imported = _extensions.FirstOrDefault(
                e => string.Equals(e.Extension.Identifier, extension.Identifier, StringComparison.OrdinalIgnoreCase));

            return imported ?? TryLoadFolder(target)
                ?? throw new SnipDoManifestException("导入后无法读取扩展。");
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (IOException)
                {
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Imports an already-unpacked extension folder. SnipDo itself keeps installed
    /// extensions as folders holding a data.json manifest (no .pbar), so the folder
    /// picker has to accept that layout too.
    /// </summary>
    public async Task<LoadedExtension> ImportFolderAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folderPath))
        {
            throw new SnipDoManifestException("目录不存在：" + folderPath);
        }

        var manifestPath = FindManifest(folderPath);
        if (manifestPath is null)
        {
            throw new SnipDoManifestException("这个目录里没有扩展清单（extension.json 或 data.json）。");
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifestRoot = Path.GetDirectoryName(manifestPath)!;
        var extension = SnipDoManifestParser.Parse(json, manifestRoot);

        Directory.CreateDirectory(_extensionsDirectory);
        var target = ResolveTargetDirectory(extension);
        AssertTargetIdentity(target, extension.Identifier);

        // F-02: copy into staging first, so a failure during the copy cannot destroy the
        // installed package; the store is only touched by the backup-protected swap below.
        var staging = Path.Combine(_extensionsDirectory, ".import-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await Task.Run(() => CopyTree(manifestRoot, staging), cancellationToken).ConfigureAwait(false);

            var backup = Path.Combine(_extensionsDirectory, ".backup-" + Guid.NewGuid().ToString("N")[..8]);
            var hasExisting = Directory.Exists(target);
            if (hasExisting)
            {
                Directory.Move(target, backup);
            }

            try
            {
                Directory.Move(staging, target);
            }
            catch
            {
                if (hasExisting && !Directory.Exists(target))
                {
                    Directory.Move(backup, target);
                }

                throw;
            }

            if (hasExisting && Directory.Exists(backup))
            {
                SafePath.DeleteInside(_extensionsDirectory, backup);
            }

            Reload();
            return _extensions.FirstOrDefault(
                       e => string.Equals(e.Extension.Identifier, extension.Identifier, StringComparison.OrdinalIgnoreCase))
                   ?? TryLoadFolder(target)
                   ?? throw new SnipDoManifestException("导入后无法读取扩展。");
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (IOException)
                {
                }
            }

            throw;
        }
    }

    /// <summary>Maps an extension identifier to a folder inside the store (path safety only).</summary>
    private string ResolveTargetDirectory(SnipDoExtension extension)
    {
        var folderName = SafePath.SlugOrFallback(extension.Identifier);
        var target = Path.Combine(_extensionsDirectory, folderName);
        if (!SafePath.IsInside(_extensionsDirectory, target))
        {
            throw new SnipDoManifestException("扩展标识无法安全地映射为目录名：" + extension.Identifier);
        }

        return target;
    }

    /// <summary>
    /// The folder name is derived from the identifier by a lossy slug, so two different
    /// identifiers can map to the same directory (F-05: "audit/a" and "audit-a" both became
    /// "audit-a"). Only an extension that already owns this folder may replace it; anything
    /// else — including a folder whose manifest cannot be read — is refused instead of deleted.
    /// </summary>
    private void AssertTargetIdentity(string target, string identifier)
    {
        if (!Directory.Exists(target))
        {
            return;
        }

        var existing = TryLoadFolder(target);
        if (existing is null
            || !string.Equals(existing.Extension.Identifier, identifier, StringComparison.OrdinalIgnoreCase))
        {
            throw new SnipDoManifestException(
                "目录 " + Path.GetFileName(target) + " 已被另一个扩展占用（标识不符），导入已取消，原有扩展未修改。");
        }
    }

    public bool Remove(string identifier)
    {
        EnsureLoaded();
        var match = _extensions.FirstOrDefault(
            e => string.Equals(e.Extension.Identifier, identifier, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return false;
        }

        try
        {
            if (Directory.Exists(match.FolderPath))
            {
                SafePath.DeleteInside(_extensionsDirectory, match.FolderPath);
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        Reload();
        return true;
    }

    private LoadedExtension? TryLoadFolder(string folder)
    {
        try
        {
            var manifestPath = FindManifest(folder);
            if (manifestPath is null)
            {
                return null;
            }

            var json = File.ReadAllText(manifestPath);
            var extension = SnipDoManifestParser.Parse(json, Path.GetDirectoryName(manifestPath)!);
            return new LoadedExtension(extension, folder);
        }
        // UnauthorizedAccessException: a manifest (or its folder) denied by ACL must skip the
        // extension, not escape into every right-click (G-20 — the catch used to let it through).
        catch (Exception ex) when (ex is SnipDoManifestException or IOException or UnauthorizedAccessException or JsonException)
        {
            _log($"跳过扩展 {Path.GetFileName(folder)}: {ex.Message}");
            return null;
        }
    }

    private static void Extract(string packagePath, string staging)
    {
        Directory.CreateDirectory(staging);
        using var archive = ZipFile.OpenRead(packagePath);
        ArchiveLimits.Validate(archive);

        var budget = new ArchiveLimits.Budget();
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/');
            if (relative.Contains("..", StringComparison.Ordinal))
            {
                throw new SnipDoManifestException("压缩包包含越界路径：" + entry.FullName);
            }

            // Reject rooted entries ("C:/x", "/x", "\\server\share") before combining, since
            // Path.Combine would silently discard the staging prefix for them.
            if (relative.StartsWith('/') || Path.IsPathRooted(entry.FullName))
            {
                throw new SnipDoManifestException("压缩包包含绝对路径：" + entry.FullName);
            }

            var destination = Path.GetFullPath(Path.Combine(staging, relative));
            if (!SafePath.IsInside(staging, destination))
            {
                throw new SnipDoManifestException("压缩包包含越界路径：" + entry.FullName);
            }

            if (relative.EndsWith('/'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            budget.Charge(entry, CopyEntry(entry, destination));
        }
    }

    /// <summary>
    /// Streams one entry to disk while counting the bytes actually written, so a lying central
    /// directory cannot get a bomb past the pre-flight check.
    /// </summary>
    private static long CopyEntry(ZipArchiveEntry entry, string destination)
    {
        using var source = entry.Open();
        using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long written = 0;

        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            written += read;
            if (written > ArchiveLimits.MaxEntryBytes)
            {
                throw new InvalidDataException($"压缩包内单个文件过大（{entry.FullName}）。");
            }

            target.Write(buffer, 0, read);
        }

        return written;
    }

    /// <summary>
    /// Copies an extension source tree into a fresh target directory. The caller owns the
    /// swap: this never deletes or overwrites an existing store folder, so a failure here
    /// leaves the installed package untouched (F-02).
    /// </summary>
    private static void CopyTree(string source, string target)
    {
        var sourceFull = Path.GetFullPath(source);
        var targetFull = Path.GetFullPath(target);

        // Copying into a folder nested under the source would re-enumerate its own output
        // forever (or inflate the tree on every pass), so refuse that shape outright.
        if (SafePath.IsInside(sourceFull, targetFull))
        {
            throw new SnipDoManifestException("扩展目录不能被复制到它自己的子目录里。");
        }

        if (Directory.Exists(targetFull))
        {
            throw new SnipDoManifestException("导入暂存目录已存在：" + targetFull);
        }

        Directory.CreateDirectory(targetFull);
        foreach (var file in EnumerateFilesShallow(sourceFull))
        {
            var relative = Path.GetRelativePath(sourceFull, file);
            if (relative.Contains("..", StringComparison.Ordinal))
            {
                throw new SnipDoManifestException("扩展目录包含越界路径：" + file);
            }

            var destination = Path.Combine(targetFull, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>
    /// Enumerates files with a bounded walk instead of <c>SearchOption.AllDirectories</c>, so a
    /// pathological (or looping) folder cannot hang the import or exhaust memory.
    /// </summary>
    private static IEnumerable<string> EnumerateFilesShallow(string root, int maxDepth = 4, int maxFiles = 4096)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var count = 0;

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();

            foreach (var file in Directory.EnumerateFiles(current))
            {
                if (++count > maxFiles)
                {
                    throw new SnipDoManifestException($"扩展目录文件过多（超过 {maxFiles} 个）。");
                }

                yield return file;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var child in Directory.EnumerateDirectories(current))
            {
                queue.Enqueue((child, depth + 1));
            }
        }
    }

    /// <summary>
    /// SnipDo packages put the manifest at the root, but some ship it one folder deep
    /// or under a non-standard name, so prefer a shallow search for a .json file.
    /// </summary>
    private static string? FindManifest(string root)
    {
        var preferred = Path.Combine(root, ManifestHint);
        if (File.Exists(preferred))
        {
            return preferred;
        }

        // Installed SnipDo bundles use "data.json" instead of "extension.json".
        var installed = Path.Combine(root, InstalledManifestHint);
        if (File.Exists(installed))
        {
            return installed;
        }

        var candidates = EnumerateFilesShallow(root, maxDepth: 3, maxFiles: 2000)
            .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(path => path.Length)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (Path.GetFileName(candidate).StartsWith("package", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }
}

/// <summary>A parsed extension plus where it lives on disk.</summary>
public sealed record LoadedExtension(SnipDoExtension Extension, string FolderPath);
