# FileInteractionMCP

A C# MCP server (stdio) that gives coding agents sandboxed file tools the built-in Read/Edit/Write set lacks.
Solution: `FileInteractionMCP.slnx`. Project: `FileInteractionMCP/`. Build with `dotnet build`.

## Using the server's tools in this repo

The server is registered in `.mcp.json` as `fileinteraction` and points at the Debug build output.
`.mcp.json` is untracked because it holds absolute local paths; copy `.mcp.json.example` to `.mcp.json`
and fill in the paths for this machine.
`--claude-scratchpad` adds the parent of Claude Code's per-session scratchpad directories for this
project (`<temp>\claude\<project slug>`, derived from the first root and created if missing) as a root,
so the tools can write to the scratchpad too.
After changing server code, run `dotnet build` and restart the MCP server (`/mcp`) before the change is live.

When the `fileinteraction` tools are available, use them instead of shell commands for:
- deleting, moving, copying, creating directories, appending: `fs_delete`, `fs_move`, `fs_copy`, `fs_mkdir`, `fs_append`
- edits by line number: `edit_lines` (always pass `expected`)
- several exact replacements in one file at once: `edit_batch`
- regex substitution in one or many files: `regex_replace` (dry run first, then `dryRun=false`)
- checking or fixing encoding and line endings: `fs_info`, `fs_convert`
- looking at lines around a known symbol or string in one file: `read_around`; end of a file: `read_tail`
- reverting a change the server made: `undo` with the `undoId` the tool returned, or `undo_list`

When `edit_batch` or `edit_lines` rejects an edit, the error message contains the exact text or line range to retry with. Use it instead of re-reading the whole file.

Keep using the built-in Read, Grep and Glob for reading whole files and searching across files; the server does not duplicate them.

## Layout

- `Sandbox/` root allowlist, symlink resolution, deny patterns, trash location
- `Text/` encoding + line-ending preserving load/save, unified diff
- `Tools/` the MCP tool classes and their result records
