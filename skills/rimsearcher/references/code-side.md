# The code side

The game's C#. `rimsearcher sources sync` writes one tree per mod and, alongside the C#, a
copy of the assemblies it came from and a table of the call sites in them. Every command
below reads that one build, so the C#, the IL and the call graph never disagree with each
other.

`rimsearcher sources list` says which trees are `current`, `stale` or `never built`, and
which have the assembly copies and the call table. A query against a tree that was never
built returns zero rows.

None of this is needed to answer a question about def data.

## Which command answers what

| The question | Command |
|---|---|
| Where is this type, and what is it | `rimsearcher types <Name>` |
| What derives from this / implements this interface | `rimsearcher types <Name> --derived` (`--transitive` for the whole subtree) |
| What does it derive from | `rimsearcher types <Name> --bases` |
| Which subclasses override this member | `rimsearcher types <Base> --derived --transitive --declares <Member>` |
| What is in this type | `rimsearcher members <Type>` — `--member-kind`, `--static`, `--virtual`, `--abstract`, `--overrides`, `--access` |
| Where does an inherited member come from | `rimsearcher members <Type> --inherited` — rows say which type declares each |
| The body of a member, or of several | `rimsearcher read <File>.cs --member <name> --member <other>` — blocks come out in the order given |
| The instructions of one method | `rimsearcher il <Type>.<Member>` — `--state-machine` for an iterator or async method |
| Who calls this method | `rimsearcher callers <Type>.<Member>` |
| What does this method call | `rimsearcher callers <Type>.<Member> --callees` |
| A code *shape* across all files | `rimsearcher code-search <regex>` |

`types` / `members` / `il` / `callers` read the assembly's metadata, so they are exact —
`members --virtual` is the metadata bit, not a guess at the word `virtual` in the text.

`read` and `code-search` read the decompiled text instead, which is where a shape like
`public\s+(?:virtual\s+)?void\s+Notify_\w+\(` across every file is answerable and nothing
else is.

## What needs the DecompilerServer MCP

Three things, and only these. The MCP is a separate program
([decompiler-mcp.md](decompiler-mcp.md)); whether it is installed is a fact about the
environment.

- **Usages of a field or a type**, as opposed to calls to a method. The call table records
  method calls only, so `where is this field read` has no exact answer here —
  `code-search` matches the name as text.
- **Reading members spread across many files in one call.** `batch_get_decompiled_source`
  takes a list of member ids and finds the files itself; `read` takes several files and
  several `--member` names per run, but every name is looked for in every file given.
- **Comparing two versions of an assembly.** Nothing on this side loads two builds at once.

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
  **braces, not C#**; recheck with `members`, which reads the metadata instead.
- **A compiler-generated type has no file in the tree.** Iterator state machines and
  closure holders are turned back into `yield` and lambdas by the decompiler, so `read`
  cannot reach them and `il` is the only way in.
- **`callers` on an override is usually zero, and that is not an absence.** A `callvirt`
  records the method named at the call site, so a call written against the base type counts
  against the base. The zero says so and names the base to ask instead.
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
  find is usually inherited — `members <Type> --inherited` says where it is declared. Trees
  are named by packageId (`vanilla` = the game); `sources list` is the roster.
