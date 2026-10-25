# FileInteractionMCP

A sandboxed file-manipulation MCP server for coding agents, written in C# on the official
[MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk). It gives an agent the file
operations its built-in tools are missing, so it stops reaching for `sed`, `perl -pi`, `rm -rf`
and friends to get ordinary editing done.

## The problem

Agentic coding tools such as Claude Code ship a small, safe set of file primitives: read a file,
replace one exact string, write a whole file, search with ripgrep. Those cover most of a coding
session. They do not cover all of it, and when they fall short the agent falls back to the shell.

That fallback happens for predictable reasons:

- **There is no delete, move, rename, copy, or append tool.** Every one of those goes through a
  shell command.
- **The edit tool needs an exact, unique string.** A tab where the agent expected spaces, a CRLF
  file, or trailing whitespace makes it fail with a bare "not found". After two or three failures
  the agent reaches for a regex in the shell.
- **There is no line-range addressing.** Insert at line 40, delete lines 12 to 30, or apply five
  edits atomically are not expressible, so they get scripted.
- **There is no regex or multi-file substitution.** Renaming a symbol across twenty files is a
  one-liner in Perl, so that is what gets used.

Shell-based file I/O is the worst way to do any of this:

- **It is hard to authorize.** A permission rule for `sed -i` cannot be made safe, because the
  behavior is in the arguments. A dedicated tool has fixed behavior you audited once.
- **It mangles files.** `sed` and `perl` do not know about byte-order marks, UTF-16, or Windows
  line endings. A one-line change can rewrite every line ending in the file.
- **It is unsandboxed.** The command can touch any path the user can, including `.git`, `.env`
  files and anything outside the project.
- **It bypasses the agent's own bookkeeping.** The built-in edit tool tracks what has been read
  and detects concurrent modification. A shell edit is invisible to that tracking.
- **It has no preview and no undo.** The change is applied before anyone sees a diff.

## What this server does instead

Every tool here is a fixed, reviewable operation behind a single sandbox:

- **Sandboxed paths.** Every path is resolved through the same guard: symlinks and junctions are
  followed, the final target must sit inside an allowed root, deny patterns block `.git`, `.env*`
  and key material on every path segment, and the trash directory is off-limits.
- **Format preservation.** Text files are loaded with their encoding, byte-order mark and dominant
  line ending detected, and written back the same way. UTF-8, UTF-8 with BOM, UTF-16 and Latin-1
  are handled. Mixed line endings are reported, not silently normalized.
- **Atomic writes.** Content goes to a temp file in the same directory, then renames over the
  target. Multi-edit and multi-file operations are all-or-nothing.
- **Diff on every change, dry run on every editing tool.** The agent can see what will happen,
  then apply.
- **Useful failure messages.** When an exact match fails, the response locates the closest text
  in the file. If it differs only in whitespace or indentation it returns the exact lines to use.
  If a line number went stale, it says where the expected text lives now.
- **Persistent undo.** Every write, delete and move returns an undo id. The history lives on
  disk, so it survives restarts and ids keep counting up. Reverting is itself recorded, so it
  can be undone.
- **No duplication of what already works.** There is no read-whole-file, glob or grep here.
  The built-in tools for those are good.

## Tools

| Tool | Purpose |
|------|---------|
| `fs_roots` | Show roots, trash location, deny patterns and limits |
| `fs_info` | Size, timestamp, encoding, BOM, line-ending counts, line count, trailing newline |
| `fs_convert` | Change encoding and/or line endings, normalize mixed endings, add final newline |
| `fs_mkdir` | Create a directory and any missing parents |
| `fs_delete` | Move a file or directory to trash (restorable), or delete permanently |
| `fs_move` | Move or rename (including case-only renames), across drives if needed; git is not told, so case-only renames need `git mv` afterwards |
| `fs_copy` | Copy a file or a directory tree |
| `fs_append` | Append text, creating the file if missing |
| `edit_lines` | Replace, delete, or insert by 1-based line range, guarded by `expected` text |
| `edit_batch` | Several exact search/replace pairs applied atomically, with occurrence counts |
| `regex_replace` | .NET regex substitution over one file or a glob, dry run by default |
| `read_around` | Numbered lines around each match of a pattern in one file |
| `read_tail` | Last N lines with line numbers |
| `undo_list` | Changes that can still be reverted, with ids |
| `undo` | Revert a change by id, or the latest |

### Examples of what the agent sees

An `edit_batch` whose `oldText` has the wrong indentation:

```
Edit 0 failed; no changes written. oldText not found. The same text exists at
lines 9-12 but with different whitespace or indentation. Use this exact text as oldText:
9:         public void Reset()
10:        {
11:            Count = 0;
12:        }
```

An `edit_lines` whose `expected` text no longer sits at the given line:

```
Lines 5-6 do not match `expected`; no changes written. Actual content:
5:     public class Widget
6:     {
The expected text is now at lines 9-10; the file has shifted. Retry with startLine=9.
```

A `regex_replace` dry run returns one unified diff per file and a total, and writes nothing until
called again with `dryRun=false`.

## Security model

The sandbox has one permission level. A path is either fully allowed for every tool or refused.

- **Roots** come from `--root` arguments (repeatable) and the `FIMCP_ROOTS` environment variable.
  With neither, the process working directory is the single root. Roots must exist at startup and
  are fixed for the life of the process. Relative paths resolve against the first root.
  `--claude-scratchpad` adds one more: the folder Claude Code keeps this project's per-session
  scratchpads in, `<temp>\claude\<slug>`, where the slug is the first root's full path with every
  non-alphanumeric character replaced by `-`. It is created at startup if missing.
- **Symlink resolution** happens segment by segment. A junction inside a root that points outside
  it is rejected before any write.
- **Deny patterns** default to `.git`, `.svn`, `.hg`, `.ssh`, `.env`, `.env.*`, `*.pem`, `*.key`,
  `*.pfx`, `*.p12`, `id_rsa*`, `id_ed25519*` and apply to reads as well as writes. Add more with
  `--deny`.
- **Roots themselves** cannot be deleted or moved.
- **Bulk operations** refuse to run when more files would change than `maxFiles` (default 20, hard
  cap `--max-bulk-files`). `regex_replace` scans every file its glob matches but counts only files
  that contain at least one match against the cap, so a wide sweep that touches few files is not
  refused. A file where the replacement reproduces the existing text is reported separately and
  is not written. It excludes `bin`, `obj`, `.git`, `.vs` and `node_modules` by default. Globs
  accept `{a,b}` alternation, and a glob that matches no files is an error rather than an empty
  result.
- **Size cap** of 20 MB (`--max-file-mb`) on text operations. Binary files are refused by text tools.

The server runs as the invoking user with no elevation, so operating-system permissions still
apply underneath. On top, MCP clients expose each tool as a separate permission entry
(`mcp__fileinteraction__fs_delete` and so on in Claude Code), which is where per-operation
allow, ask or deny policy belongs.

## How it runs

This is a stdio server. The MCP client starts it as a child process, speaks JSON-RPC over its
stdin and stdout, and kills it when the session ends. There is no service or daemon. Each client
session gets its own instance with its own roots. Trash contents and the undo journal are files
under the trash directory, so they outlive the process: undo ids keep counting across restarts
and reconnects, `undo` with no id reverts the newest entry even if an earlier session made it,
and the newest 100 entries are kept. Servers that share a trash directory share the journal and
take turns through a lock file; each one lists and reverts only entries whose paths lie inside
its own roots. On startup the server deletes snapshot folders no entry refers to. Trashed files
are never removed automatically.

```
FileInteractionMCP.exe --root D:\path\to\project [--root other] [--trash dir]
                       [--deny pattern] [--max-file-mb 20] [--max-bulk-files 50]
                       [--claude-scratchpad]
```

The default trash is `%LOCALAPPDATA%\FileInteractionMCP\trash` (`FIMCP_TRASH` overrides it).

### Claude Code

Add a `.mcp.json` to the project:

```json
{
  "mcpServers": {
    "fileinteraction": {
      "type": "stdio",
      "command": "D:\\path\\to\\FileInteractionMCP\\bin\\Debug\\net10.0\\win-x64\\FileInteractionMCP.exe",
      "args": ["--root", "D:\\path\\to\\project", "--claude-scratchpad"]
    }
  }
}
```

Any other MCP client that supports stdio servers can be configured the same way.

## Building

Requires the .NET 10 SDK.

```
dotnet build
```

The executable lands under `FileInteractionMCP/bin/Debug/net10.0/<rid>/`. After changing server
code, rebuild and reconnect the client (`/mcp` in Claude Code) so the new process is used.

## Layout

```
FileInteractionMCP/
  Sandbox/   root allowlist, symlink resolution, deny patterns, persistent undo journal, shared file ops
  Text/      encoding and line-ending preserving load/save, unified diff, fuzzy matching
  Tools/     the MCP tool classes and their result records
  Program.cs host setup and tool registration
```

## Relationship to other MCP file servers

The official filesystem server offers an edit tool with dry-run diffs and roots-based sandboxing,
but no delete, no line-range edits and no regex, and it re-implements read and search. Desktop
Commander has the best editing ergonomics of the field but bundles a terminal and documents that
its directory restrictions can be bypassed. Symbol-level editors such as Serena solve a different
problem, structural edits through a language server, and can run alongside this one.

This server deliberately covers only the gap between the built-in primitives and the shell.

## Roadmap

- Read-only roots, so reference directories can be inspected but never written.
- Symbol-level C# edits through Roslyn (replace method body, insert after member).
- Optional post-edit validation hook, such as a syntax parse of the changed file.

## License

[MIT](LICENSE.txt)
