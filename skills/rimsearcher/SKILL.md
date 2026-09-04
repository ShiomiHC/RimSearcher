---
name: rimsearcher
description: Answer questions about RimWorld's defs and C# — what a def contains after patches and inheritance, which defs use a class or a value, what a field can be set to, and where a symbol lives in the game's code. Use whenever a task involves RimWorld modding, Def XML, or the game's assemblies.
---

# RimSearcher

Two sources of truth. **The snapshot**: a database of every def the game had in memory at
export time — patches applied, inheritance resolved, code-generated defs included. Query it
with the `rimsearcher` CLI. **The assemblies**: the game's compiled C#, read either by the
CLI over a decompiled tree or by the DecompilerServer MCP, a program of its own. One layer
comes from the mods' XML instead of memory — inheritance, discarded by the game before
export. `inherit` walks it as a tree; `get --defaults`'s `xml` column climbs the same
parent chain to say whether a line is written here or by an ancestor.

Every command and option is in `<command> --help` and
[references/cli-reference.md](references/cli-reference.md); worked examples and edges in
[references/usage-notes.md](references/usage-notes.md). This page carries only what neither
of those tells you at the moment you need it.

Global options (`--snapshot`, `--db`, `--json`, `--config`) go **after** the command name.

## Pick the tool by the question

| The question | Where it is answered |
|---|---|
| What does this def actually contain? | `rimsearcher get <defName>` |
| Which C# class does this def actually run? | `rimsearcher get <defName>` — the `*Class` rows |
| Does the vanilla XML write this line — Replace or Add? | `rimsearcher get <defName> --defaults` — the `xml` column: `here` / `parent` / `no` / `under <container>`, and a `+patch` suffix when another mod's patch put the line there (Replace still finds it; your patch then depends on that mod staying loaded). **`no` is determined, not a path-shape maybe** — what exactly it denies depends on the exporter, and `code_default` below carries that. A snapshot without that column says so. `get --help` carries the rest: how list entries join back to def-name tags, and what `under` leaves undecided. |
| What is this called? I only know part. | `rimsearcher search <words>` |
| Which defs use this class / value? | `rimsearcher where <field> <value>` |
| Which defs pick this class with `Class="…"`? | `rimsearcher where Class <ClassName>` |
| Same, but only within one def type | add `--type <DefType>` — it works on `where`, `values`, `search` and `get`. The def type is never the first positional: `where HediffDef compClass X` reads `HediffDef` as a *field path* and answers a different question. |
| One field across a whole batch of defs | `rimsearcher where <field>` with **no value** — one flat row per def that has it. Never `list` + a `get` per name: that is N processes for one table, and `get` nests its output per def while `where` does not. |
| What can this field be set to? | `rimsearcher values <field>` |
| What fields does this def type have? | `rimsearcher fields <DefType>` |
| Everything of one kind | `rimsearcher list <DefType>` + `--find <text>`; no type = the def types |
| Which saved mod lists name this mod? | `rimsearcher modlist show --find <text>` |
| What inherits from this / vice versa? | `rimsearcher inherit <name>` |
| What is this worth / what does it cost to make? | `rimsearcher economy <defName>` — not a def field, `get` cannot answer it |
| UI text ↔ translation key | `rimsearcher keyed <key or phrase>` |
| Which UI text is untranslated? | `rimsearcher keyed --empty-translation` with no query |
| The game's C#: bodies, callers, overrides, hierarchy | the DecompilerServer MCP (`mcp__decompiler__*`), a separate program — exact where it is present. `code-side.md` gives the CLI's own answer to each, and names the one it cannot take |
| A code *shape* across all files | `rimsearcher code-search <regex>` |
| The text of one file, member, or line range | `rimsearcher read <file> --member <name>` |

The code side has its own two pages: which of the two tools answers what, plus the CLI's
traps, in [references/code-side.md](references/code-side.md); the MCP itself in
[references/decompiler-mcp.md](references/decompiler-mcp.md).

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
  A bare name matches the last segment whole (`where genSteps` never sees
  `extraGenSteps[N]`, while `fields BiomeDef --path-contains enStep` finds both), but a
  dotted one is raw text that does not stop at a `.` — `where graphicData.shaderType` also
  collects `swimmingGraphicData.shaderType`. `--exact-path` pins the whole path, with `[]`
  standing for any index. This changes the answer, not the row count.
- **`get --path-contains` and `where --value` match substrings too** — `--path-contains soundImpact`
  also returns `soundImpactDefault`, opposite meaning.
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
  a snapshot named `vanilla`.

## What the output cannot tell you

The CLI explains its own tables, zeros and boundaries as it prints them — read what it says
rather than assuming. The ones it has no way to state:

- **`code_default` decides what a value is worth**, and the column prints only `yes`/`no`.
  `no` = something set it (differs from a fresh instance). `yes` = the snapshot **cannot
  tell** whether anyone set it — quoting a `yes` row as "this def sets X" is the top
  confident-wrong answer here — and **"so the def did not set it, it comes from the class
  default" is the same error facing the other way**. An XML line whose value happens to
  equal the default is indistinguishable from no line at all, so neither direction is
  available **from this column** — the `xml` column beside it, when the snapshot has one, does
  tell them apart (`here` = an XML line writing that same value, `no` = the XML does not write
  it), which is the one place that question is answerable. What `no` denies is printed next to
  the table: `read after every patch ran` means the merged XML, so `no` really means no line
  reaches this field; `read before patches ran` means only the pre-patch XML on disk, where a
  line another mod's patch put there also reads `no`. Reading the C# constructor shows where the default *could* come from, never
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
  `code-search` reads and prints everything too: none of `--limit`,
  `--max-per-file` and `--max-files` carries a default, and only `--max-files` can make an
  answer partial — passing it a number turns the count into `at least N`.
- **Exit codes**: `0` ran, `1` zero rows, `2` usage error, `70` tool defect. **Chain with `;`,
  never `&&`** — an informative zero otherwise drops what you queued after it. A `;` chain reports only the last code, so read the output.
  **Everything lands on stdout except a usage error** — the reasoning behind a zero
  included. `2` is the exception: its message is on stderr with stdout empty, so
  `2>/dev/null` turns a mistyped option into a silent empty result.
- **`--json`**: root object; prose moves into `notes` as `{kind, text}`; the data key
  depends on the command but is always present when produced, empty array and all. **A
  missing key means you asked the wrong key, never an empty result.** Key map:
  usage-notes; `<command> --help` is authoritative.
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
  abstract, which layer declares a field (`--path-contains`), and how many patches target a
  node by `Name=`. Its field **values** are still the snapshot's, already post-patch —
  nothing here sees a def before a PatchOperation.
- **A `list` def type is a storage bucket, not a runtime class.** Multi-class buckets get a
  `class` column and `--own-class`. Most buckets hold one class — there `--own-class`
  narrows nothing and the behaviour lives on a nested `Class="…"` field instead: **`where
  Class` territory, not `--own-class`**.
- **`where Class`** reaches that nested runtime type, but only where it **differs from the
  declared type** — a field running exactly what its C# declares is not indexed under
  `Class` at all, and a snapshot may hold only part of this dimension (the zero says which). So a zero is about
  the index, never "no def runs it": confirm with `code-search "class <Name>\b"`. The same
  holds for a class no def drives at all — code `new`s it directly, and the construction
  site is the answer.
- **`values <field>` already answers "which def types have this field"** — its `def_types`
  row names them with `n of m` coverage. `fields <DefType>` goes the other way and needs
  the type up front.
- **Null-valued fields never enter the index** — absent even from `--defaults`, so on
  `get`/`where` absence is not evidence the type lacks the field. `fields <DefType>
  --path-contains <text>` is the one place that is settled for you rather than left to the
  declaring class: it keeps **the type declares it, no def has a value** apart from **the
  type does not declare such a field either**, off a list of declared paths that does not
  depend on any def having a value. A snapshot that carries no such list says so.

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

**Use text search last**: `where`/`values` are exact over resolved data; `code-search`
matches identically-named things from unrelated types.


