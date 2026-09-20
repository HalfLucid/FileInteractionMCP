using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileInteractionMCP.Sandbox;

/// <summary>
/// Persistent history of changes made through this server. Every writing tool opens an entry,
/// snapshots what it is about to touch, performs the write, then commits. Reverting an entry is
/// itself recorded, so an undo can be undone.
/// <para>
/// The journal lives under the trash directory as <c>undo/journal.json</c> with one snapshot folder
/// per entry, so ids keep counting across server restarts and every process that shares the trash
/// directory sees the same stack. Each read-modify-write happens under a lock file, so concurrent
/// servers never hand out the same id or clobber each other's entries. Entries are visible to a
/// server only when every path they touch lies inside one of its roots; the id sequence is global.
/// </para>
/// </summary>
public sealed class UndoJournal(SandboxOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OrphanAge = TimeSpan.FromHours(1);

    private readonly string _undoRoot = Path.Combine(options.TrashDirectory, "undo");

    private string JournalPath => Path.Combine(_undoRoot, "journal.json");
    private string LockPath => Path.Combine(_undoRoot, "journal.lock");

    /// <summary>Oldest entries beyond this many are dropped, with their snapshots, on every commit.</summary>
    public int MaxEntries { get; init; } = 100;

    public UndoEntry Begin(string tool, string summary) => new()
    {
        Tool = tool,
        Summary = summary,
        SnapshotDirectory = Path.Combine(_undoRoot, Guid.NewGuid().ToString("N")),
    };

    /// <summary>Records the entry. Returns its id, or null when the entry captured nothing.</summary>
    public int? Commit(UndoEntry entry)
    {
        if (entry.Actions.Count == 0)
        {
            entry.DeleteSnapshots();
            return null;
        }

        using var _ = AcquireLock();
        var state = Load();
        Append(state, entry);
        Save(state);
        return entry.Id;
    }

    /// <summary>Entries this server may revert, oldest first.</summary>
    public IReadOnlyList<UndoEntry> Entries
    {
        get
        {
            using var _ = AcquireLock();
            return Load().Entries.Where(InScope).ToArray();
        }
    }

    /// <summary>
    /// Reverts the entry with the given id, or the newest visible entry when null, and records the
    /// reversal as a new entry. Returns the reverted entry and the reversal.
    /// </summary>
    public (UndoEntry Entry, UndoEntry Reversal) Revert(int? id)
    {
        using var _ = AcquireLock();
        var state = Load();

        var entry = id is null
            ? state.Entries.LastOrDefault(InScope)
            : state.Entries.FirstOrDefault(e => e.Id == id);

        if (entry is null)
        {
            throw new InvalidOperationException(id is null ? "Nothing to undo." : $"No undo entry #{id}.");
        }

        if (!InScope(entry))
        {
            throw new InvalidOperationException(
                $"Undo entry #{id} touches paths outside this server's roots and cannot be reverted here.");
        }

        var reversal = Begin("undo", $"undo #{entry.Id} ({entry.Tool}: {entry.Summary})");
        var completed = false;

        try
        {
            for (int i = entry.Actions.Count - 1; i >= 0; i--)
            {
                switch (entry.Actions[i])
                {
                    case RestoreContent restore:
                        reversal.CaptureContent(restore.Path);
                        if (restore.Snapshot is null)
                        {
                            if (File.Exists(restore.Path))
                            {
                                File.Delete(restore.Path);
                            }
                        }
                        else
                        {
                            FileOps.WriteAtomic(restore.Path, File.ReadAllBytes(restore.Snapshot));
                        }

                        break;

                    case MoveBack move:
                        if (!FileOps.IsCaseOnlyRename(move.CurrentPath, move.OriginalPath)
                            && (File.Exists(move.OriginalPath) || Directory.Exists(move.OriginalPath)))
                        {
                            throw new IOException($"Cannot undo move: {move.OriginalPath} exists again.");
                        }

                        var isDir = Directory.Exists(move.CurrentPath);
                        if (!isDir && !File.Exists(move.CurrentPath))
                        {
                            throw new IOException($"Cannot undo move: {move.CurrentPath} no longer exists.");
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(move.OriginalPath)!);
                        FileOps.Move(move.CurrentPath, move.OriginalPath, isDir, overwrite: false);
                        reversal.RecordMove(move.OriginalPath, move.CurrentPath);
                        break;
                }
            }

            completed = true;
        }
        finally
        {
            if (completed)
            {
                state.Entries.RemoveAll(e => e.Id == entry.Id);
                entry.DeleteSnapshots();
            }

            // Even a partial reversal is recorded so the files it touched can be put back.
            if (reversal.Actions.Count > 0)
            {
                Append(state, reversal);
            }
            else
            {
                reversal.DeleteSnapshots();
            }

            Save(state);
        }

        return (entry, reversal);
    }

    /// <summary>
    /// Deletes snapshot folders no journal entry refers to, including those left by earlier versions
    /// that kept one folder per session. Folders younger than an hour are left alone because another
    /// server may be filling one for an entry it has not committed yet. Returns the number removed.
    /// </summary>
    public int PruneOrphans()
    {
        if (!Directory.Exists(_undoRoot))
        {
            return 0;
        }

        using var _ = AcquireLock();
        var referenced = new HashSet<string>(
            Load().Entries.Select(e => e.SnapshotDirectory), PathGuard.PathComparer);
        var cutoff = DateTime.UtcNow - OrphanAge;

        var removed = 0;
        foreach (var dir in Directory.GetDirectories(_undoRoot))
        {
            if (referenced.Contains(dir) || Directory.GetLastWriteTimeUtc(dir) > cutoff)
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort; try again next startup.
            }
        }

        return removed;
    }

    private bool InScope(UndoEntry entry) =>
        entry.Paths.All(p => options.Roots.Any(root => PathGuard.IsWithin(p, root)));

    private void Append(JournalState state, UndoEntry entry)
    {
        entry.Id = state.NextId++;
        state.Entries.Add(entry);

        while (state.Entries.Count > MaxEntries)
        {
            var oldest = state.Entries[0];
            state.Entries.RemoveAt(0);
            oldest.DeleteSnapshots();
        }
    }

    private FileStream AcquireLock()
    {
        Directory.CreateDirectory(_undoRoot);
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }

    private JournalState Load()
    {
        if (!File.Exists(JournalPath))
        {
            return new JournalState();
        }

        try
        {
            return JsonSerializer.Deserialize<JournalState>(File.ReadAllBytes(JournalPath), JsonOptions)
                ?? new JournalState();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection and start a fresh journal beside it.
            File.Move(JournalPath, $"{JournalPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            return new JournalState();
        }
    }

    private void Save(JournalState state) =>
        FileOps.WriteAtomic(JournalPath, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions));

    private sealed class JournalState
    {
        public int NextId { get; set; } = 1;
        public List<UndoEntry> Entries { get; set; } = [];
    }
}

public sealed class UndoEntry
{
    public int Id { get; set; }
    public required string Tool { get; init; }
    public required string Summary { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public required string SnapshotDirectory { get; init; }
    public List<UndoAction> Actions { get; init; } = [];

    [JsonIgnore]
    public IEnumerable<string> Paths => Actions.Select(a => a switch
    {
        RestoreContent r => r.Path,
        MoveBack m => m.OriginalPath,
        _ => string.Empty,
    });

    /// <summary>Snapshot a file's current bytes, or note that it does not exist yet.</summary>
    public void CaptureContent(string realPath)
    {
        if (!File.Exists(realPath))
        {
            Actions.Add(new RestoreContent(realPath, null));
            return;
        }

        Directory.CreateDirectory(SnapshotDirectory);
        var snapshot = Path.Combine(SnapshotDirectory, $"{Actions.Count}-{Path.GetFileName(realPath)}");
        File.Copy(realPath, snapshot, overwrite: true);
        Actions.Add(new RestoreContent(realPath, snapshot));
    }

    /// <summary>Note that <paramref name="from"/> was moved to <paramref name="to"/>.</summary>
    public void RecordMove(string from, string to) => Actions.Add(new MoveBack(to, from));

    internal void DeleteSnapshots()
    {
        if (!Directory.Exists(SnapshotDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(SnapshotDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; leftovers are removed by PruneOrphans at a later startup.
        }
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RestoreContent), "restore")]
[JsonDerivedType(typeof(MoveBack), "move")]
public abstract record UndoAction;

/// <summary>Put <see cref="Snapshot"/> back at <see cref="Path"/>, or delete the file when the snapshot is null.</summary>
public sealed record RestoreContent(string Path, string? Snapshot) : UndoAction;

/// <summary>Move whatever is now at <see cref="CurrentPath"/> back to <see cref="OriginalPath"/>.</summary>
public sealed record MoveBack(string CurrentPath, string OriginalPath) : UndoAction;
