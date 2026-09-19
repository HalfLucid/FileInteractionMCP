namespace FileInteractionMCP.Sandbox;

/// <summary>
/// Session-scoped history of changes made through this server. Every writing tool opens an entry,
/// snapshots what it is about to touch, performs the write, then commits. Reverting an entry is
/// itself recorded, so an undo can be undone. Snapshots live under the trash directory and the
/// history is lost when the server process exits.
/// </summary>
public sealed class UndoJournal
{
    private readonly object _gate = new();
    private readonly List<UndoEntry> _entries = [];
    private readonly string _directory;
    private int _nextId = 1;

    public int MaxEntries { get; init; } = 30;

    public UndoJournal(SandboxOptions options)
    {
        _directory = Path.Combine(
            options.TrashDirectory, "undo", $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
    }

    public UndoEntry Begin(string tool, string summary) =>
        new(Path.Combine(_directory, Guid.NewGuid().ToString("N")), tool, summary);

    /// <summary>Records the entry. Returns its id, or null when the entry captured nothing.</summary>
    public int? Commit(UndoEntry entry)
    {
        if (entry.Actions.Count == 0)
        {
            entry.DeleteSnapshots();
            return null;
        }

        lock (_gate)
        {
            entry.Id = _nextId++;
            _entries.Add(entry);

            while (_entries.Count > MaxEntries)
            {
                var oldest = _entries[0];
                _entries.RemoveAt(0);
                oldest.DeleteSnapshots();
            }

            return entry.Id;
        }
    }

    public IReadOnlyList<UndoEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public UndoEntry? Find(int? id)
    {
        lock (_gate)
        {
            return id is null ? _entries.LastOrDefault() : _entries.FirstOrDefault(e => e.Id == id);
        }
    }

    /// <summary>Reverts every action in the entry, newest first, and records the reversal as a new entry.</summary>
    public UndoEntry Revert(UndoEntry entry)
    {
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
                        if (File.Exists(move.OriginalPath) || Directory.Exists(move.OriginalPath))
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
                lock (_gate)
                {
                    _entries.Remove(entry);
                }

                entry.DeleteSnapshots();
            }

            // Even a partial reversal is recorded so the files it touched can be put back.
            Commit(reversal);
        }

        return reversal;
    }
}

public sealed class UndoEntry(string snapshotDirectory, string tool, string summary)
{
    public int Id { get; internal set; }
    public string Tool => tool;
    public string Summary => summary;
    public DateTime Timestamp { get; } = DateTime.Now;
    public List<UndoAction> Actions { get; } = [];

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

        Directory.CreateDirectory(snapshotDirectory);
        var snapshot = Path.Combine(snapshotDirectory, $"{Actions.Count}-{Path.GetFileName(realPath)}");
        File.Copy(realPath, snapshot, overwrite: true);
        Actions.Add(new RestoreContent(realPath, snapshot));
    }

    /// <summary>Note that <paramref name="from"/> was moved to <paramref name="to"/>.</summary>
    public void RecordMove(string from, string to) => Actions.Add(new MoveBack(to, from));

    internal void DeleteSnapshots()
    {
        if (!Directory.Exists(snapshotDirectory))
        {
            return;
        }

        try
        {
            Directory.Delete(snapshotDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; leftovers sit under the trash directory.
        }
    }
}

public abstract record UndoAction;

/// <summary>Put <see cref="Snapshot"/> back at <see cref="Path"/>, or delete the file when the snapshot is null.</summary>
public sealed record RestoreContent(string Path, string? Snapshot) : UndoAction;

/// <summary>Move whatever is now at <see cref="CurrentPath"/> back to <see cref="OriginalPath"/>.</summary>
public sealed record MoveBack(string CurrentPath, string OriginalPath) : UndoAction;
