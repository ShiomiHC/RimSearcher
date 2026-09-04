# The code side

The game's C#, reached two ways. The DecompilerServer MCP (its own page:
[decompiler-mcp.md](decompiler-mcp.md)) is a separate program: rimsearcher neither ships
it nor depends on it, and whether it is installed is a fact about the environment, not
about either tool. The CLI's `code-search` / `read` need nothing outside rimsearcher —
they read a decompiled tree that `rimsearcher sources sync` writes.

This page is which of the two answers what, plus the traps in the CLI half. None of it is
needed to answer a question about def data.

## Which of the two

The MCP is exact: it reads metadata, not text. The CLI reads text, over whatever
`sources sync` has written — `rimsearcher sources list` says which trees are `current`,
`stale` or `never built`; a query against a tree that was never built returns zero rows.

Only the last row is beyond the CLI outright. The rest it answers approximately, and the
approximation is better than it sounds: decompiled output is machine-generated and
regularly formatted, so a declaration never wraps mid-signature the way hand-written code
does — one `code-search "class \w+ : ThingComp\b"` catches the direct subclasses. To confirm that
on a tree you have not used before, run `"^\s*: ThingComp\b"` after it: that is the shape a
wrapped declaration would leave behind, and rows there mean the first pass missed some.

| The question | With the MCP | With the CLI alone |
|---|---|---|
| The body of one member | `get_decompiled_source` | `rimsearcher read <File>.cs --member <name>` |
| Where is this type declared | `search_types` | `rimsearcher code-search "class <Name>\b"`, then `read` that file `--outline` |
| What derives from this | `find_derived_types` (`transitive: true` for the whole subtree) | `rimsearcher code-search "class \w+ : <Base>\b"` — direct children only; iterate for the subtree |
| Who overrides this member | `get_overrides` | `rimsearcher code-search "override [\w<>, \[\]]+ <Member>\("` |
| Who calls this method | `find_callers` | nothing exact — `code-search` matches *text*; same-named members collide. Read the hits; never report the count as a caller count. |
| Anything at the opcode level | `get_il` | **nothing.** `Call` vs `Callvirt`, transpiler targets — invisible in decompiled C#. Say the question cannot be answered. |

Going the other way, a shape only text can express — `public\s+(?:virtual\s+)?void\s+Notify_\w+\(`
across every file — is the CLI's, and the MCP has no equivalent.


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
  numbers — "how many methods" wants the first. Of the three switches that cut the answer,
  `--limit` and `--max-per-file` only shape what is printed (the count stays exact); **only
  `--max-files` shortens the scan**, turning the count into `at least N`. None of the three
  carries a default — every file the glob selects is read and every match is printed until you
  pass one of them a positive number, and none of them takes `all`. Decompiled text has lost
  comments and local variable names (parameters and members survive); a member you cannot
  find is usually inherited — follow the `: Base`. Trees are named by packageId (`vanilla` =
  the game); `sources list` is the roster.
