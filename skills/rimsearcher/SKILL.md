---
name: rimsearcher
description: Answer questions about RimWorld's defs and C# — what a def contains after patches and inheritance, which defs use a class or a value, what a field can be set to, and where a symbol lives in the game's code. Use whenever a task involves RimWorld modding, Def XML, or the game's assemblies.
---

# RimSearcher

Two sources of truth. **The snapshot**: a database of every def the game had in memory at
export time — patches applied, inheritance resolved, code-generated defs included. Query it
with the `rimsearcher` CLI. **The assemblies**: the game's compiled C#, read by the same CLI
over a decompiled tree — its source text, its IL and its call graph all from one build. One layer
comes from the mods' XML instead of memory — inheritance, discarded by the game before
export. `inherit` walks it as a tree; `get --defaults`'s `xml` column climbs the same
parent chain to say whether a line is written here or by an ancestor.

Every command and option is in `<command> --help` and
[references/cli-reference.md](references/cli-reference.md); worked examples and edges in
[references/usage-notes.md](references/usage-notes.md). This page carries the contracts:
what an answer means, and where a query stops being able to answer.

Global options (`--snapshot`, `--db`, `--json`, `--quiet`, `--config`) go **after** the command name.

## Pick the tool by the question

| The question | Where it is answered |
|---|---|
| What does this def actually contain? | `rimsearcher get <defName>` — several names at once print one block each, in the order given |
| Every def of one type, in full | `rimsearcher get --type <DefType> --json` with **no def name** — one block per def, def-name order, one process |
| Which C# class does this def actually run? | `rimsearcher get <defName>` — the `*Class` rows |
| Does the vanilla XML write this line — Replace or Add? | `rimsearcher get <defName> --defaults` — the `xml` column: `here` / `parent` / `not-written` / `under <container>`, and a `+patch` suffix when another mod's patch put the line there (Replace still finds it; your patch then depends on that mod staying loaded). What exactly it denies depends on the exporter, and `code_default` below carries that. A snapshot without that column says so. `get --help` carries the rest: how list entries join back to def-name tags, and what `under` leaves undecided. |
| What is this called? I only know part. | `rimsearcher search <words>` |
| Which defs use this class / value? | `rimsearcher where <field> <value>` |
| Which defs pick this class with `Class="…"`? | `rimsearcher where Class <ClassName>` |
| Same, but only within one def type | add `--type <DefType>` — it works on `where`, `values`, `search` and `get`. The def type is never the first positional: `where HediffDef compClass X` reads `HediffDef` as a *field path* and answers a different question. |
| One field across a whole batch of defs | `rimsearcher where <field>` with **no value** — one flat row per def that has it. Not `list` + a `get` per name: `get` nests its output per def while `where` does not. When you do need the whole field table of many defs, pass every name to one `get`, or `get --type <DefType>` for the type; never one process per name. |
| What can this field be set to? | `rimsearcher values <field>...` |
| What fields does this def type have? | `rimsearcher fields <DefType>...` |
| Everything of one kind | `rimsearcher list <DefType>...` + `--find <text>`; no type = the def types |
| Which saved mod lists name this mod? | `rimsearcher modlist show --find <text>` |
| What inherits from this / vice versa? | `rimsearcher inherit <name>...` |
| What is this worth / what does it cost to make? | `rimsearcher economy <defName>...` — not a def field, `get` cannot answer it. Several names go into the same three tables, so the rows line up for comparison. Leave the name out for the whole priced layer in one call: every row carries the same keys, minus `costChain` and `recipes`, which only the named form computes |
| UI text ↔ translation key | `rimsearcher keyed <key or phrase>...` |
| Which UI text is untranslated? | `rimsearcher keyed --empty-translation` with no query |
| Where a C# type lives, and what it derives from | `rimsearcher types <Name>...` — `--derived`, `--bases`, `--transitive` |
| What is in a type, and which members are virtual or overridden | `rimsearcher members <Type>...` — the filters are metadata bits, not keywords in the text |
| Which types declare a member of this name | `rimsearcher members --name <Member>` with no type — reads metadata across every type, where `code-search` would scan the text of every file |
| Which subclasses override this member | `rimsearcher types <Base> --derived --transitive --declares <Member>` |
| Who calls this method | `rimsearcher callers <Type>.<Member>...` — `--callees` for the other direction |
| The instructions of one method (transpilers) | `rimsearcher il <Type>.<Member>...` — `--state-machine` for an iterator or async method |
| A code *shape* across all files | `rimsearcher code-search <regex>` |
| The text of one file, member, or line range | `rimsearcher read <file>... --member <name>` |

**Anything you look up by name takes several names in one call** — `get`, `economy`,
`inherit`, `types`, `members`, `il`, `callers`, `read`, `list`, `fields`, `values`, `keyed`:
`rimsearcher values compClass thingClass`, `rimsearcher fields ThingDef HediffDef`,
`rimsearcher read A.cs B.cs --outline`. Each name gets its own count line, and `--limit`
and `--offset` apply to each one separately rather than to the batch. The rows land in one
table, with a column naming which argument each row answers (`get` is the exception: it
prints a block per def, as it does for one name). A name that misses does not sink the
others — the call still exits 0, and only an all-miss exits 1. **Do not run one process per
name.**

The code side has its own page — which command answers what, plus the traps — in
[references/code-side.md](references/code-side.md). Three questions need the
DecompilerServer MCP, a separate program: usages of a *field or type* rather than calls to a
method, reading many members in one call, and comparing two builds. Those are in
[references/decompiler-mcp.md](references/decompiler-mcp.md); nothing else is.

## If your instinct is to grep the XML, stop

PatchOperations rewrite the XML on disk, inheritance merges it, and thousands of defs
(`Meat_*`, `Corpse_*`, blueprints) exist in no file. Translate the intent:

| Instead of | Ask |
|---|---|
| grep `<defName>Bullet_` | `rimsearcher search Bullet_` |
| grep `<li Class="CompProperties_AmbientSound">` | `rimsearcher where compClass CompAmbientSound` |
| grep a `Class="…"` to see which defs pick it | `rimsearcher where Class <ClassName>` |
| grep a `<thingClass>` to see who uses it | `rimsearcher where thingClass <ClassName>` |
| grep to find what values a tag takes | `rimsearcher values <tag>` |
| grep `Name="BaseBullet"` for the abstract parent | `rimsearcher inherit BaseBullet` |

Second row: the XML names the **properties** class, the def field holds the resolved
**comp** class — asking with the properties name costs a redirect round trip.

## Out of range

- **"How should this XML be written?"** No path from runtime objects back to authorable
  source — read the declaring C# class; its fields are what the XML parser binds to.
  `get`'s `source` line is a bare, unverified file name.
- **Files on disk.** `values texPath` works (paths are def fields), but nothing reads the
  file system — an `ls` question.

## Defaults that bite

Each of these is a case where the obvious move returns a clean, complete-looking answer to
a different question. None of them announces itself.

- **`where`'s path is matched from the end; every `--path-contains` filter is a substring.**
  A bare name matches the last segment whole (`where label` never collects
  `cannotEnterLabel`; `fields ThingDef --path-contains label` does, because it is a
  substring), and a dotted one matches whole segments the same way —
  `where graphicData.shaderType` does not collect `swimmingGraphicData.shaderType`.
  It is still a suffix, so segments can sit above it — the output names those shapes;
  rerun as `where <that shape> --exact-path` to keep that one alone.
  `[]` stands for any index on the positional path of `where` and `values`, and on every
  `--path-contains`: `comps[].props.energyMax` matches every `comps[N].props.energyMax`.
- **`get --path-contains` and `where --value` match substrings too** — `--path-contains soundImpact`
  also returns `soundImpactDefault`, opposite meaning. On `where --value` without a field path
  the two kinds are separate columns, `defs_exact` and `defs_other`, so a row can be entirely
  substring hits; `--exact` drops them and leaves one `defs`.
- **One defName can belong to several def types, and `get` then prints one block per def** —
  `HospitalBed` is both a ThingDef and a ResearchProjectDef. Under `--json` that is a
  `defs[]` of more than one entry in **no guaranteed order**, so `defs[0].fields` reads
  *another def's* fields and shows up as "this def has no such field", never as an error.
  Pick the entry by its `defs[].def.def_type`, or pin it with `--type <DefType>` — which exits `1`
  naming the types it does have, rather than handing back a different def. A `boundary`
  note announces the collision every time, including under `--type`.
- **Reverse-look-up field names, never guess.** `where --value <value>` reports which paths
  hold the value. A guessed field name that happens to exist returns a clean,
  complete-looking table for the wrong field — the most expensive failure here.
- **`--scope vanilla`** (also `core`/`base`/`official`) = every module Ludeon ships — **not**
  a snapshot named `vanilla`. It selects mods in the snapshot, so only the snapshot
  commands take it.
- **The code side narrows by source tree, not by scope** —
  `rimsearcher code-search <regex> --source <tree>`, and `rimsearcher sources list` names
  the trees. The two narrowing options mean different things and neither accepts the
  other's name, so a spelling carried over from the other side is rejected, not reused.

## What the output cannot tell you

The CLI explains its own tables, zeros and boundaries as it prints them. The ones it has no
way to state:

- **`code_default` decides what a value is worth**, and the column prints only `yes`/`no`.
  `no` = something set it (differs from a fresh instance). `yes` = the snapshot **cannot
  tell** whether anyone set it: an XML line whose value happens to
  equal the default is indistinguishable from no line at all, so neither direction is
  available **from this column** — the `xml` column beside it, when the snapshot has one, does
  tell them apart (`here` = an XML line writing that same value, `not-written` = no such line),
  which is the one place that question is answerable. What `not-written` denies is printed next to
  the table itself — which XML this snapshot read, before or after the patches ran.
  Reading the C# constructor shows where the default *could* come from, never
  whether the XML says it too. `unknown` = type not constructible. Exemptions cut both
  ways: rules that *read* the value (thresholds, comparisons) answer fine from a `yes` row
  — the value is real either way; `compClass`/`thingClass`/`workerClass` are usually
  constructor-assigned, so a `yes` there is **no signal in either direction** — and a `no`
  beside it is just as ordinary, reached by more than one route. Neither value says who
  mounted the comp; the `mod` column and the block's `Class` row do. `yes` rows hide by
  default (a line says how many); `--defaults` shows them; `--path-contains` always shows a
  named field.
- **A value most defs of the type also carry is inherited or engine-filled far more often
  than authored** — so a `no` on one of those is still not the def author's decision. The
  line under `get`'s table names them; what it means for authorship is not in it.
- **Leave `--limit` out and you get every row**; `read` with no `--lines` gives the whole file.
  Pass a number to shorten an answer on purpose; the count line then says how many exist, and
  on `list`, `search`, `where`, `values`, `fields` and `keyed` an `--offset` walks the rest.
  `code-search` reads and prints every line too: none of `--limit`,
  `--max-per-file` and `--max-files` carries a default, and only `--max-files` can make an
  answer partial — passing it a number turns the count into `at least N`. What it does shorten
  without being asked is an over-long line: decompiled code puts a whole table on one line, so
  `--max-line-chars` (240) prints such a line as the neighbourhood of its matches with `…` for
  the rest. That is width, not rows — `--max-line-chars 0` prints them whole, and `--json`
  carries the whole line either way.
- **Exit codes**: `0` ran, `1` zero rows, `2` usage error, `70` tool defect. **Chain with `;`,
  never `&&`** — an informative zero otherwise drops what you queued after it. A `;` chain reports only the last code, so read the output.
  With several names on one `get`, `0` means at least one of them printed, not that all did:
  a name that matched nothing has its own note and no object in `defs`, so check the array
  against the names you asked for rather than the code. All of them missing is `1`.
  **Everything lands on stdout except a usage error** — the reasoning behind a zero
  included. `2` is the exception: its message is on stderr with stdout empty, so
  `2>/dev/null` turns a mistyped option into a silent empty result.
- **Batch shapes keep every per-block sentence, so do not pipe them either.** `get A B C`
  and `get --type <DefType>` print one block per def, and each block carries its own counts,
  truncation warnings and footnotes; a `head` keeps the first def's and drops the rest, which
  reads as "the others had nothing to declare". Take the whole thing, in `--json` when it is
  long: the notes stay addressable there, and per-def notes name the def they belong to.
  When the pipeline wants only the rows, `--quiet` drops the prose on purpose rather than
  by accident — a `head` that cuts it away leaves the same stdout either way, and only one
  of the two says which it was.
- **`--quiet`** (alias `--data-only`): stdout carries the data blocks and nothing else —
  no notices, no footnotes, no snapshot tag. Reach for it when a pipeline counts lines or
  slices columns and the prose would land in the middle of that. A query that finds nothing
  then prints no stdout at all and still exits 1: that emptiness is the prose being withheld,
  not evidence that the thing is absent.
- **The prose has a second home.** With `RIMSEARCHER_RUN_LOG` pointing at a path, every run
  appends one JSON line there — every notice with its kind, its counts and the blocks on
  either side, plus the exit code, whether `--quiet` was asked for, and the usage-error
  message (that one never goes through the report, so it has its own key). Unset, nothing is
  written and the run does no extra IO. What a pipe or `--quiet` keeps off stdout is still
  addressable there; a tool that reads it can tell prose that was filtered out from prose the
  caller asked to drop.
- **`--json`**: root object; prose moves into `notes` as `{kind, text}`; the data key
  depends on the command but is always present, empty array and all — an empty result never
  shows up as a missing key. Keys **beside** that one can be conditional; each command's
  `--help` says when. Key map: usage-notes. `where`, `values` and `fields` add
  `completeness` when some def in scope had its export cut short — it carries the scope in
  words, the count, one row per def type, and a ready command to list them. Its absence
  means no def in scope lost fields.
- **Anything read by a program takes `--json`.** The text tables are laid out for a human
  reader: columns are padded to width, and a column whose value repeats in every row is
  lifted out into a `Same in every row, not repeated below:` line and then **missing from
  the rows**. Splitting those rows on whitespace yields a different number of fields
  depending on the data, and the lifted column reads as absent rather than constant — both
  failures produce plausible values rather than an error.
- **The same key name is not the same row shape.** Every command's key holds flat rows
  except two: `get`'s `defs` is one nested object per def (`{def, fields, translations}`)
  and `inherit`'s `nodes` is one per XML node (`{node, ancestors, children?, witnesses?}`).
  So `search` and `get` both answer under `defs` while nesting differently, and a def's
  field table is `defs[i].fields`, never a `fields` key at the root. Take the shape from the
  command's own `--help`, and
  **index the one real path rather than probing** — a script written as
  `j.fields || j.rows || []` turns a wrong guess into an empty array, and empty is exactly
  what a def with no such field looks like. That failure writes a complete-looking file and
  exits 0.

## Layers a query cannot cross

- **`search`** covers def names, labels, descriptions and the translations injected onto
  defs — both languages, so an English term finds its def on a Chinese snapshot. It does
  **not** cover C# class names (→ `where compClass <Class>`) or the UI strings under
  `Languages/*/Keyed` (→ `keyed <phrase>`); a zero result names which one you hit — the
  layer the name actually sits on, query already filled in, instead of reciting that list
  back at you.
- **A def's translations answer to the same field paths as its fields**, so a field path
  copied out of the field table — `stages[0].label` — selects the same place in both tables.
  A language file spells that same slot two other ways: `stages.0.label`, which
  `get <defName> --path` folds back and so reaches both tables too, and `stages.<handle>.label`, which
  reaches the translations table only — a handle is not a field path. The key is what a
  language file must say, and it appears in the row's
  `key` cell **only when it differs** from the path.
- **`keyed` is the only road to screen text** — captions, alerts, tooltips are keyed
  translations belonging to no def, unreachable by `search`/`get`/`where`. Both directions:
  key → displayed text, phrase in either language → keys. Only `in effect` rows are what
  the game displays; `on disk` rows mostly come from installed-but-disabled mods.
  `--empty-translation` with no query lists every untranslated key — **do not invent a
  stand-in query**: `""`, `*`, `.` are not wildcards, and a real word silently answers a
  different question.
- **Prices are computed, not stored** — `economy` is the only road to them. A market value
  can be derived from a recipe and a cost is a cost list expanded recursively, so none of
  these numbers is a def field and `get` returns nothing for them. Read a blank cell as *the
  game cannot work this out*, never as zero: `calcState` keeps the four reasons apart, and a
  `fallbackMarketValue` of `0` under `calcState=ok` means the sum came out empty, not that
  the thing is free. `chainEndShare` of 1 means that row's `profit` is one hand-written
  number minus a few others. A snapshot may not hold this layer at all — `economy` then says
  which of three things happened instead of answering, and **there is no way around it**: the
  indexed `marketValue` is the XML base value, not the computed price, and no field holds cost
  or profit, so ranking by it answers a different question with nothing to say so.
- **Abstract parents are not defs**: `get` cannot reach them — it names `inherit` instead.
  `inherit` answers four things off the XML layer: who inherits from whom, which nodes are
  abstract, which layers carry a field (`--path-contains`), and how many patches target a
  node by `Name=`. The tree and those patch counts are the XML **before** PatchOperations ran;
  the field values shown beside them are the snapshot's, already post-patch. Counting
  witnesses is not the same as finding where a field is declared — the footnote on that
  output says what the count does and does not settle.
- **A `list` def type is a storage bucket, not a runtime class.** Multi-class buckets get a
  `class` column and `--class`. Most buckets hold one class — there `--class`
  narrows nothing and the behaviour lives on a nested `Class="…"` field instead: **`where
  Class` territory, not `--class`**.
- **`where Class`** reaches that nested runtime type, but only where it **differs from the
  declared type** — a field running exactly what its C# declares is not indexed under
  `Class` at all, and a snapshot may hold only part of this dimension (the zero says which). So a zero is about
  the index, never "no def runs it": confirm with `code-search "class <Name>\b"`. The same
  holds for a class no def drives at all — code `new`s it directly, and the construction
  site is the answer.
- **`values <field>` already answers "which def types have this field"** — its `def_types`
  row names them, each with how many defs of that type hold a value at the path out of how
  many defs that type has. `fields <DefType>` goes the other way and needs
  the type up front.
- **Null-valued fields never enter the index** — absent even from `--defaults`, so on
  `get`/`where` absence is not evidence the type lacks the field. `fields <DefType>
  --path-contains <text>` is the one place that is settled for you rather than left to the
  declaring class: it keeps **the type declares it, no def has a value** apart from **none
  of the fields the type itself declares has it either**, off a list of declared paths that
  does not depend on any def having a value. That set is collected to a bounded nesting depth
  and the notice says how deep it reached — a field nested past that is outside what it
  measured. A snapshot that carries no such list says so. The same command also says when
  the text is a **value** on that type rather than a field name, and when it found no value
  either — so a silent answer never stands in for a checked one.

## Snapshots

One export = one game version, one ordered mod list, one language; several coexist.
`snapshot list` shows them, `--snapshot <name>` picks per command, `snapshot use <name>`
sticks; `snapshot status` compares it against the installed game, naming which mods'
Defs/Patches XML moved on disk; `snapshot diff <old> <new>` compares two snapshots'
resolved defs and fields.

Making one, how generations of a name rotate, and what the staleness check misses:
[references/snapshots.md](references/snapshots.md).

**A complete count is complete for the snapshot, not the installed game**: on a Core-only
snapshot, `1 def` means one in Core, and **no line says so** — the one boundary here that
never announces itself. A def that is in the game but not in the snapshot means the mod was
not enabled at export; `rimsearcher mods` lists coverage.

**Use text search last**, on both sides. For def data `where`/`values` are exact over
resolved data. For code, `types`/`members`/`callers` read metadata — a name they match is
the symbol, while `code-search` matches identically-named things from unrelated types and
counts them all.


