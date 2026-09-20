using System.ComponentModel;
using FileInteractionMCP.Sandbox;
using ModelContextProtocol.Server;
using static FileInteractionMCP.Tools.ToolSupport;

namespace FileInteractionMCP.Tools;

/// <summary>Session undo over every change this server has made.</summary>
[McpServerToolType]
internal sealed class HistoryTools(UndoJournal journal)
{
    [McpServerTool(Name = "undo_list", ReadOnly = true, Idempotent = true)]
    [Description("List the changes made through this server that can still be reverted, oldest first, with the undo id each writing tool returned. History is kept on disk, so it survives restarts and reconnects and ids keep counting up; only entries whose paths lie inside this server's roots are shown.")]
    public object List() => new UndoListResult(
        true,
        journal.Entries.Select(e => new UndoEntryInfo(e.Id, e.Tool, e.Summary, e.Timestamp, e.Paths.Distinct().ToArray())).ToArray());

    [McpServerTool(Name = "undo", Destructive = true)]
    [Description("Revert a change made through this server: restores the snapshot taken before an edit, or moves a deleted/moved item back. Defaults to the most recent change, including one made before a restart or reconnect. Reverting an older entry restores that entry's snapshot, which discards later edits to the same files. The reversal is itself recorded, so it can be undone (redoId).")]
    public object Undo(
        [Description("Undo id from a tool result or undo_list. Omit for the latest change.")] int? id = null) => Run(() =>
    {
        var (entry, reversal) = journal.Revert(id);
        return new UndoResult(true, entry.Id, entry.Tool, entry.Summary, entry.Paths.Distinct().ToArray(), reversal.Id == 0 ? null : reversal.Id);
    });
}
