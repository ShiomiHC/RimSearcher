---
name: rimsearcher
description: Answer questions about RimWorld's defs and C# — what a def contains after patches and inheritance, which defs use a class or a value, what a field can be set to, and where a symbol lives in the game's code. Use whenever a task involves RimWorld modding, Def XML, or the game's assemblies.
---

# RimSearcher

Two sources of truth. **The snapshot**: a database of every def the game had in memory at
export time — patches applied, inheritance resolved, code-generated defs included. Query it
with the `rimsearcher` CLI. **The assemblies**: the game's compiled C#, via the
DecompilerServer MCP (`mcp__decompiler__*`). One layer comes from the mods' XML instead of
memory — inheritance, discarded by the game before export; `inherit` alone reads it.

Contracts live here; mechanics, edges and worked examples in
[references/usage-notes.md](references/usage-notes.md); every command and option in
`<command> --help` or [references/cli-reference.md](references/cli-reference.md).

## Pick the tool by the question

| The question | Where it is answered |
|---|---|
| What does this def actually contain? | `rimsearcher get <defName>` |
| Which C# class does this def actually run? | `rimsearcher get <defName>` — the `*Class` rows |
| What is this called? I only know part. | `rimsearcher search <words>` |
| Which defs use this class / value? | `rimsearcher where <field> <value>` |
| Which defs pick this class with `Class="…"`? | `rimsearcher where Class <ClassName>` |
| What can this field be set to? | `rimsearcher values <field>` |
| What fields does this def type have? | `rimsearcher fields <DefType>` |
| Everything of one kind | `rimsearcher list <DefType>` + `--find <text>`; no type = the def types |
| Which saved mod lists name this mod? | `rimsearcher modlist show --find <text>` |
| What inherits from this / vice versa? | `rimsearcher inherit <name>` |
| UI text ↔ translation key | `rimsearcher keyed <key or phrase>` |
| Which UI text is untranslated? | `rimsearcher keyed --empty-translation` with no query |
| The game's C#: bodies, callers, overrides, hierarchy | `mcp__decompiler__get_decompiled_source`, `find_callers`, `get_overrides`, `find_derived_types`, `search_types` |
| A code *shape* across all files | `rimsearcher code-search <regex>` |
| The text of one file, member, or line range | `rimsearcher read <file> --member <name>` |

The MCP is often not connected — a normal state, not an error. CLI substitutes:

| Instead of | Without the MCP |
|---|---|
| `get_decompiled_source` | `rimsearcher read <File>.cs --member <name>` |
| `search_types` | `rimsearcher code-search "class <Name>\b"`, then `read` that file `--outline` |
| `find_derived_types` | `rimsearcher code-search "class \w+ : <Base>\b"` |
| `get_overrides` | `rimsearcher code-search "override [\w<>, \[\]]+ <Member>\("` |
| `find_callers` | nothing exact — `code-search` matches *text*; same-named members collide. Read the hits; never report the count as a caller count. |
| `get_il` | **nothing.** Opcode-level questions (`Call` vs `Callvirt`, transpiler targets) are invisible in decompiled C#. Say the question cannot be answered. |

## Out of range

- **"How should this XML be written?"** No path from runtime objects back to authorable
  source — read the declaring C# class; its fields are what the XML parser binds to.
  `get`'s `source` line is a bare, unverified file name.
- **Files on disk.** `values texPath` works (paths are def fields), but nothing reads the
  file system — an `ls` question.

## If your instinct is to grep the XML, stop

PatchOperations rewrite the XML on disk, inheritance merges it, and thousands of defs
(`Meat_*`, `Corpse_*`, blueprints) exist in no file. Translate the intent:

| Old habit | Now |
|---|---|
| grep `<defName>Bullet_` | `rimsearcher search Bullet_` |
| grep `<li Class="CompProperties_AmbientSound">` | `rimsearcher where compClass CompAmbientSound` |
| grep a `Class="…"` to see which defs pick it | `rimsearcher where Class <ClassName>` |
| grep a `<thingClass>` to see who uses it | `rimsearcher where thingClass <ClassName>` |
| grep to find what values a tag takes | `rimsearcher values <tag>` |
| grep `Name="BaseBullet"` for the abstract parent | `rimsearcher inherit BaseBullet` |

Second row: the XML names the **properties** class, the def field holds the resolved
**comp** class — asking with the properties name costs a redirect round trip.

`code-search` searches **decompiled C#, never Defs** — pointed at a data question it says
so. A `--file-glob` glob containing `/` is matched against the whole path, which is
`<packageId>/<assembly>/<namespace dirs>/<file>.cs` — **two levels before the namespace**,
so `vanilla/Assembly-CSharp/RimWorld/*.cs`, and `vanilla/RimWorld/**` matches nothing.
`**` crosses `/` and is the safe way to skip the assembly you did not look up
(`vanilla/**/Widgets.cs`); this holds under `--source` too. No `/` matches file names at
any depth.

## Query habits

- **PowerShell: single-quote regexes.** Double quotes interpolate `$` — `"…: $name\b"`
  reaches the tool with `$name` already replaced, and `"(\w+)$"` is fine only because the
  quote follows. Backslashes survive either way (PowerShell escapes with a backtick), so
  the damage is silent and confined to `$`: the pattern that ran is not the one you wrote.
- **`code-search` is case-sensitive unless you pass `-i`** — `orbitalDebris` and
  `OrbitalDebris` are two searches, and the wrong one's zero looks like absence.
- **Narrow inside the command.** Each one has its own filters:

| Command | Narrow with |
|---|---|
| `get` | `--path-contains`, `--type`, `--defaults`, `--limit` |
| `fields` | `--path-contains`, `--offset`, `--limit` |
| `values` | `--type`, `--scope`, `--exact-path`, `--offset`, `--limit` |
| `search` | `--type`, `--scope`, `--offset`, `--limit` |
| `list` | `--find`, `--own-class`, `--scope`, `--offset`, `--limit` |
| `inherit` | `--path-contains`, `--limit` |
| `keyed` | `--empty-translation`, `--offset`, `--limit` |
| `where` | `--scope`, `--exact`, `--exact-path`, `--offset`, `--limit` |
| `code-search` | `--source`, `--file-glob`, `--max-files`, `--max-per-file`, `--limit` |
| `read` | `--member`, `--type`, `--lines`, `--outline`, `--source`, `--limit` |
| `sources sync` | `--only`, `--modlist`, `--force`, `--dry-run` |

  Reading a long result in pieces: paging commands take `--offset`; `read` `--member`/`--type`/
  `--outline`/`--lines`; `get` `--path-contains` (field order carries no meaning); `code-search`
  an anchored pattern plus `--source`/`--file-glob`. `read`'s `--limit` is the odd one out —
  it caps printed lines rather than narrowing a result set, so `--limit all` *widens*.
  `read --member` pages too, just not through `--offset`: `--limit` caps the lines and the
  count line hands back the exact `--lines a-b` to resume from, so a long member is read
  window by window.
- **Reverse-look-up field names, never guess.** `where --value <value>` reports which paths
  hold the value. A guessed field name that happens to exist returns a clean,
  complete-looking table for the wrong field — the most expensive failure here.
- **`where`'s path is matched from the end; every `--path-contains` filter is a substring.** A bare name
  matches the last segment whole (`where genSteps` never sees `extraGenSteps[N]`, while
  `fields BiomeDef --path-contains enStep` finds both), but a dotted one is raw text that does not stop
  at a `.` — `where graphicData.shaderType` also collects `swimmingGraphicData.shaderType`.
  `--exact-path` pins the whole path, with `[]` standing for any index. This changes the
  answer, not the row count.
- **The `mod` column says where the def was declared, not who wrote the value you asked
  about**, and `--scope` filters that same thing. A comp a third-party mod bolts onto a
  vanilla def stays filed under the vanilla mod: `--scope vanilla` keeps it,
  `--scope all,-vanilla` drops it — backwards from the instinct. Nothing records which mod
  authored a value.
- **`values <field>` already answers "which def types have this field"** — its `def_types`
  row names them with `n of m` coverage. `fields <DefType>` goes the other way and needs
  the type up front.
- **Give `read` the bare file name; never build a path from the namespace** — folders are
  not namespaces. `--member`/`--type`/`--outline` match **braces, not C#**: a name they
  miss is not proof of absence — recheck with `code-search` or `--lines`. A member of a
  *loaded assembly* is still the MCP's job.

## Reading the output

- **A count is always printed above the table**, in three forms: `12 defs.` — all of them;
  `12 of 347 defs` — cut off, 347 exist, and the next page is `--offset 12`;
  `at least 12 matches` — the scan stopped early, true total unknown. A `--path-contains` match count is a filter, not a truncation
  (`kind: "filter"` vs `"truncation"` in `--json`).
- **`Same in every row, not repeated below:` is part of the table** — the folded column
  still applies to every row; the first column (the row's identity) and `--json` never fold.
- **Exit codes**: `0` ran, `1` zero rows, `2` usage error, `70` tool defect. **`1` is an
  answer, not a failure** — chain with `;`, never `&&`, or an informative zero drops what
  you queued after it. A `;` chain reports only the last code, so read the output.
  **Everything lands on stdout except a usage error** — the reasoning behind a zero
  included. `2` is the exception: its message is on stderr with stdout empty, so
  `2>/dev/null` turns a mistyped option into a silent empty result.
- **A zero result names its own cause** — hidden by `--scope`, abstract parent, def type,
  class, mod, UI text, or present in another named snapshot. Read that sentence before
  concluding: "not here" ≠ "not anywhere".
- **Truncation at export**: `get` warns on the affected def, and then a missing field path
  is *not* evidence of absence. Same boundary on `where`/`values`/`fields` (counts are over
  indexed paths); cross-check with `rimsearcher snapshot truncated` (narrow `--type`,
  `--def`), which the footnote prints already filled in.
- **`code_default` decides what a value is worth.** `no` = something set it (differs from
  a fresh instance). `yes` = the snapshot **cannot tell** whether anyone set it — quoting a
  `yes` row as "this def sets X" is the top confident-wrong answer here — and **"so the def
  did not set it, it comes from the class default" is the same error facing the other way**.
  An XML line whose value happens to equal the default is indistinguishable from no line at
  all, so neither direction is available: the honest answer off a `yes` row is that this
  cannot be told from here. Reading the C# constructor shows where the default *could* come
  from, never whether the XML says it too. `unknown` = type
  not constructible. Exemptions cut both ways: rules that *read* the value (thresholds,
  comparisons) answer fine from a `yes` row — the value is real either way;
  `compClass`/`thingClass`/`workerClass` are usually
  constructor-assigned, so a `yes` there is **no signal in either direction** — and a `no`
  beside it is just as ordinary, reached by more than one route. Neither value says who mounted the
  comp; the `mod` column and the block's `Class` row do. `yes` rows hide by default (a line
  says how many); `--defaults` shows them; `--path-contains` always shows a named field.
- **The shared-value line after `get`'s table** (`soundDrop (3347)`) names values most
  defs of the type also carry — inherited or engine-filled far more often than authored,
  so a `no` row on that list is still not the def author's decision.
- **The sibling line under `--path-contains` is not decoration**: fields in one `comps[N]` block
  constrain each other, and the output names hand-set fields in the block it cut away.
- **Null-valued fields never enter the index** — absent even from `--defaults`. Absence is
  not evidence the type lacks the field; read the declaring class.
- **`--json`**: root object; prose moves into `notes` as `{kind, text}`; the data key
  depends on the command but is always present when produced, empty array and all. **A
  missing key means you asked the wrong key, never an empty result.** Key map:
  usage-notes; `<command> --help` is authoritative.

## Semantics that bite

- **`--scope vanilla`** (also `core`/`base`/`official`) = every module Ludeon ships — **not**
  a snapshot named `vanilla`; the output spells out what a scope resolved to. Global options
  (`--snapshot`, `--db`, `--json`, `--config`) go **after** the command name.
- **`search`** covers def names, labels, descriptions and the translations injected onto
  defs — both languages, so an English term finds its def on a Chinese snapshot. It does
  **not** cover C# class names (→ `where compClass <Class>`) or the UI strings under
  `Languages/*/Keyed` (→ `keyed <phrase>`); a zero result names which one you hit — the
  layer the name actually sits on, query already filled in, instead of reciting that list
  back at you.
- **`get --path-contains`/`--value` match substrings** — `--path-contains soundImpact` also returns
  `soundImpactDefault`, opposite meaning; the output says when nothing matched as a whole
  segment. `--type <DefType>` picks between same-named defs (common).
- **Abstract parents are not defs**: `get` cannot reach them — it names `inherit` instead.
  `inherit` answers four things off the XML layer: who inherits from whom, which nodes are
  abstract, which layer declares a field (`--path-contains`), and how many patches target a node by
  `Name=`. Its field **values** are still the snapshot's, already post-patch — nothing here
  sees a def before a PatchOperation. Every rule for reading those columns and counts,
  including what each of them is *not* evidence for, prints beside the table that carries it.
- **A `list` def type is a storage bucket, not a runtime class.** Multi-class buckets get a
  `class` column and `--own-class`; `list <SomeClass>` says where to look instead of "no such
  type". Most buckets hold one class — there `--own-class` narrows nothing and the behaviour
  lives on a nested `Class="…"` field instead: **`where Class` territory, not `--own-class`**.
- **`where Class`** reaches that nested runtime type, but only where it **differs from the
  declared type** — a field running exactly what its C# declares is not indexed under
  `Class` at all, and older snapshots predate parts of this dimension. So a zero is about
  the index, never "no def runs it": confirm with `code-search "class <Name>\b"`.
- **`keyed` is the only road to screen text** — captions, alerts, tooltips are keyed
  translations belonging to no def, unreachable by `search`/`get`/`where`. Both directions:
  key → displayed text, phrase in either language → keys. Only `in effect` rows are what
  the game displays; `on disk` rows mostly come from installed-but-disabled mods.
  `--empty-translation` with no query lists every untranslated key — **do not invent a stand-in
  query**: `""`, `*`, `.` are not wildcards, and a real word silently answers a different
  question.
- **`code-search` reports matches and files as two numbers** — "how many methods" wants
  the first. Of its three caps, `--limit` and `--max-per-file` only shape what is printed
  (the count stays exact); **only `--max-files` shortens the scan**, turning the count into
  `at least N` — raise it rather than looking elsewhere. Tree counts: `sources list`.
- **Decompiled text has lost comments and local variable names** (parameters and members
  survive). A member you cannot find is usually inherited — follow the `: Base`. Trees are
  named by packageId (`vanilla` = the game); `sources list` is the roster and says which
  trees are current, empty or absent.

## Snapshots

One export = one game version, one ordered mod list, one language; several coexist.
`rimsearcher snapshot list` shows them, `--snapshot <name>` picks per command,
`snapshot use <name>` sticks; `snapshot status` is the full comparison with the installed
game. Queries raise staleness themselves when they detect it. When several are registered
and you did not name one, the first line carries `[<name>]` — or `[<name> auto-detected]`
when nothing was pinned and the pick came from matching the game's mod list.

**A complete count is complete for the snapshot, not the installed game**: on a Core-only
snapshot, `1 def` means one in Core, and **no line says so** — the one boundary here that
never announces itself. Stale → `rimsearcher export --modlist <name>`, where `<name>` is
required and comes from `rimsearcher modlist list`.

## Recovering

- **In game but not in the snapshot**: the mod was not enabled at export. `rimsearcher mods`
  lists coverage; another registered snapshot holding the def is named in the zero result.
- **Answer disagrees with the game**: mod files changed since export — queries say so when
  detected, but the check is size and timestamp, so an edit preserving both, or anything
  under `Languages/`, passes unseen. Re-export before concluding the tool is wrong.
- **`where Class <ClassName>` came back zero**: a C# class can be used with no def at all —
  code `new`s it directly. Before believing a near-miss the zero result suggests, scan
  unanchored: `code-search '<ClassName>' --limit all`; the construction site is the answer.
- **Use text search last**: `where`/`values` are exact over resolved data; `code-search`
  matches identically-named things from unrelated types.

For the decompiler MCP itself: [references/decompiler-mcp.md](references/decompiler-mcp.md).
