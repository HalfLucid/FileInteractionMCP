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
    [Description("List the changes made through this server in the current session, oldest first, with the undo id each writing tool returned.")]
    public object List() => new UndoListResult(
        true,
        journal.Entries.Select(e => new UndoEntryInfo(e.Id, e.Tool, e.Summary, e.Timestamp, e.Paths.Distinct().ToArray())).ToArray());

    [McpServerTool(Name = "undo", Destructive = true)]
    [Description("Revert a change made through this server: restores the snapshot taken before an edit, or moves a deleted/moved item back. Defaults to the most recent change. Reverting an older entry restores that entry's snapshot, which discards later edits to the same files. The reversal is itself recorded, so it can be undone (redoId).")]
    public object Undo(
        [Description("Undo id from a tool result or undo_list. Omit for the latest change.")] int? id = null) => Run(() =>
    {
        var entry = journal.Find(id)
            ?? throw new InvalidOperationException(id is null ? "Nothing to undo in this session." : $"No undo entry #{id}.");

        var reversal = journal.Revert(entry);
        return new UndoResult(true, entry.Id, entry.Tool, entry.Summary, entry.Paths.Distinct().ToArray(), reversal.Id == 0 ? null : reversal.Id);
    });
}
