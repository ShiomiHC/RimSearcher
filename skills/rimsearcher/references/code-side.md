# The code side

The game's C#, reached two ways: the DecompilerServer MCP (its own page:
[decompiler-mcp.md](decompiler-mcp.md)) and the CLI's `code-search` / `read` over a
decompiled tree. This page is the CLI half plus what to do when the MCP is absent —
none of it is needed to answer a question about def data.

## Without the MCP

The MCP is often not connected — a normal state, not an error. CLI substitutes:

| Instead of | Without the MCP |
|---|---|
| `get_decompiled_source` | `rimsearcher read <File>.cs --member <name>` |
| `search_types` | `rimsearcher code-search "class <Name>\b"`, then `read` that file `--outline` |
| `find_derived_types` | `rimsearcher code-search "class \w+ : <Base>\b"` |
| `get_overrides` | `rimsearcher code-search "override [\w<>, \[\]]+ <Member>\("` |
| `find_callers` | nothing exact — `code-search` matches *text*; same-named members collide. Read the hits; never report the count as a caller count. |
| `get_il` | **nothing.** Opcode-level questions (`Call` vs `Callvirt`, transpiler targets) are invisible in decompiled C#. Say the question cannot be answered. |

## Traps

- **`code-search` is case-sensitive unless you pass `-i`** — `orbitalDebris` and
  `OrbitalDebris` are two searches, and the wrong one's zero looks like absence.
- **A `--file-glob` containing `/` is matched against the whole path**, which is
  `<packageId>/<assembly>/<namespace dirs>/<file>.cs` — **two levels before the namespace**,
  so `vanilla/Assembly-CSharp/RimWorld/*.cs`, and `vanilla/RimWorld/**` matches nothing.
  `**` crosses `/` and is the safe way to skip the assembly you did not look up
  (`vanilla/**/Widgets.cs`); this holds under `--source` too. No `/` matches file names at
  any depth.
- **A name `--member`/`--type`/`--outline` misses is not proof of absence** — they match
  **braces, not C#**; recheck with `code-search` or `--lines`. A member of a *loaded
  assembly* is still the MCP's job.
- **PowerShell: single-quote regexes.** Double quotes interpolate `$` — `"…: $name\b"`
  reaches the tool with `$name` already replaced, and `"(\w+)$"` is fine only because the
  quote follows. Backslashes survive either way (PowerShell escapes with a backtick), so
  the damage is silent and confined to `$`: the pattern that ran is not the one you wrote.


## What `code-search` counts

- **`code-search` searches decompiled C#, never Defs.** It reports matches and files as two
  numbers — "how many methods" wants the first. Of its three caps, `--limit` and
  `--max-per-file` only shape what is printed (the count stays exact); **only `--max-files`
  shortens the scan**, turning the count into `at least N`. Decompiled text has lost
  comments and local variable names (parameters and members survive); a member you cannot
  find is usually inherited — follow the `: Base`. Trees are named by packageId (`vanilla` =
  the game); `sources list` is the roster.
