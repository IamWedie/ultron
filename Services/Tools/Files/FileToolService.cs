using System.Text;

namespace Ultron.Services;

/// <summary>Arguments for the <c>file_processor</c> tool.</summary>
public sealed record FileProcessorArgs
{
    public string Action { get; init; } = "";
    public string? FilePath { get; init; }
    public string? Destination { get; init; }
    public string? NewPath { get; init; }
    public string? Target { get; init; }
    public string? Content { get; init; }
    public string? Query { get; init; }

    /// <summary>Models send the destination under any of three names.</summary>
    public string? DestinationPath =>
        FirstNonEmpty(Destination, NewPath, Target);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>
/// Owns every file operation the assistant can perform, together with the
/// security policy that bounds it and the undo entries that reverse it.
/// </summary>
/// <remarks>
/// This class has no knowledge of the UI: it receives a path policy and an undo
/// ledger through its constructor and returns a <see cref="ToolResult"/> for
/// every outcome, including the unsuccessful ones.
/// </remarks>
public sealed class FileToolService : IToolHandler
{
    private const long MaxReadBytes = 10L * 1024 * 1024;
    private const long MaxWriteTargetBytes = 10L * 1024 * 1024;
    private const long MaxDeleteBytes = 400L * 1024 * 1024;
    private const long MaxUndoTreeBytes = 200L * 1024 * 1024;
    private const int MaxUndoTreeFiles = 200;
    private const int MaxListedDirs = 40;
    private const int MaxListedFiles = 60;
    private const int MaxSearchHits = 30;
    private const int MaxSearchDirs = 2000;
    private const int MaxReadChars = 3200;

    private readonly IPathPolicy _paths;
    private readonly UndoLedger _undo;

    public FileToolService(IPathPolicy paths, UndoLedger undo)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _undo = undo ?? throw new ArgumentNullException(nameof(undo));
    }

    public string Name => "file_processor";

    public Task<ToolResult> ExecuteAsync(ToolCall toolCall, CancellationToken cancellationToken) =>
        Task.FromResult(Run(toolCall.Deserialize<FileProcessorArgs>(), cancellationToken));

    private ToolResult Run(FileProcessorArgs args, CancellationToken cancellationToken)
    {
        var action = (args.Action ?? "").Trim().ToLowerInvariant();
        if (action.Length == 0)
            return ToolResult.InvalidArguments("file_processor: missing action.");

        // A path is required by every action except a bare profile-wide search.
        string? source = null;
        if (!string.IsNullOrWhiteSpace(args.FilePath))
        {
            var verdict = _paths.Validate(args.FilePath);
            if (!verdict.Allowed)
                return ToolResult.InvalidArguments($"file_processor: {verdict.Error}");
            source = verdict.FullPath;
        }

        return action switch
        {
            "read" => Read(source),
            "write" or "create" => Write(source, args.Content ?? "", cancellationToken),
            "rename" or "move" => MoveOrRename(action, source, args.DestinationPath),
            "copy" => Copy(source, args.DestinationPath),
            "delete" => Delete(source),
            "list" or "browse" => List(source),
            "search" => Search(source, args.Query),
            _ => ToolResult.Unsupported(
                $"file_processor: unknown action '{args.Action}'. Use read, write, list, search, copy, move, rename or delete."),
        };
    }

    private ToolResult Read(string? path)
    {
        if (path is null) return ToolResult.InvalidArguments("read: missing file_path.");
        if (!File.Exists(path)) return ToolResult.NotFound($"File not found: {path}");

        try
        {
            if (new FileInfo(path).Length > MaxReadBytes)
                return ToolResult.Fail($"read refused: {Path.GetFileName(path)} exceeds the 10 MB limit.");

            var bytes = File.ReadAllBytes(path);
            var text = AsText(bytes);
            if (text is null)
                return ToolResult.Ok($"Binary file ({bytes.Length:N0} bytes, {Path.GetExtension(path)}), not shown as text.");

            return ToolResult.Ok(text.Length <= MaxReadChars
                ? text
                : text[..MaxReadChars] + $"\n[...truncated, {text.Length} chars total]");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"read failed: {ex.Message}");
        }
    }

    private ToolResult Write(string? path, string content, CancellationToken cancellationToken)
    {
        if (path is null) return ToolResult.InvalidArguments("write: missing file_path.");
        if (cancellationToken.IsCancellationRequested) return ToolResult.Cancelled("write cancelled.");

        try
        {
            var existed = File.Exists(path);
            if (existed && new FileInfo(path).Length > MaxWriteTargetBytes)
                return ToolResult.Fail($"write refused: {Path.GetFileName(path)} exceeds the 10 MB limit.");

            var original = existed ? File.ReadAllText(path) : null;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(path, content);

            _undo.Push($"write {Path.GetFileName(path)}", () =>
            {
                if (original is null)
                {
                    File.Delete(path);
                    return $"Deleted newly created {path}.";
                }
                File.WriteAllText(path, original);
                return $"Restored original content of {path}.";
            });

            return ToolResult.Ok($"Written {content.Length} chars to {path}.");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"write failed: {ex.Message}");
        }
    }

    private ToolResult MoveOrRename(string action, string? source, string? rawDestination)
    {
        if (source is null) return ToolResult.InvalidArguments($"{action}: missing file_path (source).");
        var destination = ResolveDestination(rawDestination, action);
        if (destination.Failure is not null) return destination.Failure;

        if (!File.Exists(source) && !Directory.Exists(source))
            return ToolResult.NotFound($"{action}: source not found: {source}");
        if (File.Exists(destination.Path!) || Directory.Exists(destination.Path!))
            return ToolResult.Fail($"{action}: destination already exists: {destination.Path}");

        try
        {
            var isDirectory = Directory.Exists(source);
            if (isDirectory) Directory.Move(source, destination.Path!);
            else File.Move(source, destination.Path!);

            _undo.Push($"{action} {Path.GetFileName(source)}", () =>
            {
                if (isDirectory) Directory.Move(destination.Path!, source);
                else File.Move(destination.Path!, source);
                return $"Moved {Path.GetFileName(destination.Path)} back to {source}.";
            });

            return ToolResult.Ok($"{source} -> {destination.Path}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"{action} failed: {ex.Message}");
        }
    }

    private ToolResult Copy(string? source, string? rawDestination)
    {
        if (source is null) return ToolResult.InvalidArguments("copy: missing file_path (source).");
        var destination = ResolveDestination(rawDestination, "copy");
        if (destination.Failure is not null) return destination.Failure;

        if (!File.Exists(source) && !Directory.Exists(source))
            return ToolResult.NotFound($"copy: source not found: {source}");
        if (File.Exists(destination.Path!) || Directory.Exists(destination.Path!))
            return ToolResult.Fail($"copy: destination already exists: {destination.Path}");

        try
        {
            if (Directory.Exists(source)) CopyDirectoryRecursive(source, destination.Path!);
            else File.Copy(source, destination.Path!);

            _undo.Push($"copy {Path.GetFileName(source)}", () =>
            {
                DeleteTree(destination.Path!);
                return $"Deleted the copy {destination.Path}.";
            });

            return ToolResult.Ok($"{source} -> {destination.Path}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"copy failed: {ex.Message}");
        }
    }

    private ToolResult Delete(string? path)
    {
        if (path is null) return ToolResult.InvalidArguments("delete: missing file_path.");

        // Scope says "inside the profile"; this says "but never the profile
        // folder itself".
        if (string.Equals(path, _paths.Root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail("delete refused: the user profile folder itself cannot be deleted.");

        try
        {
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > MaxDeleteBytes)
                    return ToolResult.Fail("delete refused: file exceeds 400 MB, so it could not be undone.");

                var bytes = File.ReadAllBytes(path);
                var directory = Path.GetDirectoryName(path) ?? ".";
                File.Delete(path);

                _undo.Push($"delete {Path.GetFileName(path)}", () =>
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllBytes(path, bytes);
                    return $"Restored {path}.";
                });

                return ToolResult.Ok($"deleted: {path}");
            }

            if (Directory.Exists(path))
            {
                var tree = CaptureTree(path);
                if (tree is null)
                    return ToolResult.Fail("delete refused: the folder is too large or holds too many files to be undoable.");

                Directory.Delete(path, recursive: true);
                _undo.Push($"delete folder {Path.GetFileName(path)}", () =>
                {
                    RestoreTree(path, tree);
                    return $"Restored folder {path}.";
                });

                return ToolResult.Ok($"deleted folder: {path}");
            }

            return ToolResult.NotFound($"delete: path not found: {path}");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"delete failed: {ex.Message}");
        }
    }

    private ToolResult List(string? path)
    {
        if (path is null) return ToolResult.InvalidArguments("list: missing file_path.");
        if (!Directory.Exists(path)) return ToolResult.NotFound($"Directory not found: {path}");

        try
        {
            var directories = Directory.GetDirectories(path);
            var files = Directory.GetFiles(path);
            var sb = new StringBuilder();
            sb.AppendLine($"{path}  ({directories.Length} folders, {files.Length} files)");

            foreach (var dir in directories.Take(MaxListedDirs))
                sb.AppendLine($"  [FOLDER]  {Path.GetFileName(dir)}");
            foreach (var file in files.Take(MaxListedFiles))
                sb.AppendLine($"  {new FileInfo(file).Length,12:N0}  {Path.GetFileName(file)}");

            var hidden = (directories.Length - MaxListedDirs) + (files.Length - MaxListedFiles);
            if (hidden > 0) sb.AppendLine($"  ... {hidden} more entries");

            return ToolResult.Ok(sb.ToString());
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"list failed: {ex.Message}");
        }
    }

    private ToolResult Search(string? root, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return ToolResult.InvalidArguments("search: missing query.");

        var start = root ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!Directory.Exists(start)) return ToolResult.NotFound($"Directory not found: {start}");

        try
        {
            var hits = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in EnumerateDirectories(start, MaxSearchDirs))
            {
                if (hits.Count >= MaxSearchHits) break;
                try
                {
                    foreach (var file in Directory.GetFiles(dir, "*" + query + "*"))
                    {
                        hits.Add(file);
                        if (hits.Count >= MaxSearchHits) break;
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }
            }

            var results = hits.Take(MaxSearchHits).ToList();
            return ToolResult.Ok(results.Count > 0
                ? $"Matches for \"{query}\":\n" + string.Join("\n", results)
                : $"No matches for \"{query}\" under {start}.");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"search failed: {ex.Message}");
        }
    }

    private readonly record struct DestinationResult(string? Path, ToolResult? Failure)
    {
        public static DestinationResult Ok(string path) => new(path, null);
        public static DestinationResult Invalid(ToolResult failure) => new(null, failure);
    }

    private DestinationResult ResolveDestination(string? rawDestination, string action)
    {
        if (string.IsNullOrWhiteSpace(rawDestination))
            return DestinationResult.Invalid(ToolResult.InvalidArguments($"{action}: missing destination."));

        var verdict = _paths.Validate(rawDestination);
        return verdict.Allowed
            ? DestinationResult.Ok(verdict.FullPath)
            : DestinationResult.Invalid(ToolResult.InvalidArguments($"{action}: {verdict.Error}"));
    }

    /// <summary>Null when the bytes are not plausibly text.</summary>
    private static string? AsText(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        if (bytes.AsSpan(0, Math.Min(4, bytes.Length)).SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 })) return null;
        if (bytes.AsSpan(0, Math.Min(2, bytes.Length)).SequenceEqual(new byte[] { 0x7F, 0x45 })) return null;
        foreach (var b in bytes.AsSpan(0, Math.Min(4096, bytes.Length)))
            if (b == 0) return null;
        return Encoding.UTF8.GetString(bytes);
    }

    private static IEnumerable<string> EnumerateDirectories(string root, int limit)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var seen = 0;
        while (stack.Count > 0 && seen++ < limit)
        {
            var dir = stack.Pop();
            yield return dir;
            try
            {
                foreach (var sub in Directory.GetDirectories(dir).Take(50)) stack.Push(sub);
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void CopyDirectoryRecursive(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectoryRecursive(dir, Path.Combine(destination, Path.GetFileName(dir)));
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }

    private static void DeleteTree(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed record TreeEntry(string RelativePath, bool IsDirectory, byte[]? Bytes);

    /// <summary>Null when the tree is too large to hold in memory for an undo.</summary>
    private static TreeEntry[]? CaptureTree(string root)
    {
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        var directories = Directory.GetDirectories(root, "*", SearchOption.AllDirectories);
        if (files.Length > MaxUndoTreeFiles) return null;

        long total = 0;
        foreach (var file in files)
        {
            total += new FileInfo(file).Length;
            if (total > MaxUndoTreeBytes) return null;
        }

        var entries = new List<TreeEntry>(directories.Length + files.Length);
        foreach (var dir in directories)
            entries.Add(new TreeEntry(Path.GetRelativePath(root, dir), true, null));
        foreach (var file in files)
            entries.Add(new TreeEntry(Path.GetRelativePath(root, file), false, File.ReadAllBytes(file)));
        return entries.ToArray();
    }

    private static void RestoreTree(string root, TreeEntry[] entries)
    {
        Directory.CreateDirectory(root);
        foreach (var entry in entries)
        {
            var full = Path.Combine(root, entry.RelativePath);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(full);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
            File.WriteAllBytes(full, entry.Bytes ?? Array.Empty<byte>());
        }
    }
}
