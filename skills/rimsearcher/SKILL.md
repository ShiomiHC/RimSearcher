---
name: rimsearcher
description: Answer questions about RimWorld's defs and C# — what a def contains after patches and inheritance, which defs use a class or a value, what a field can be set to, and where a symbol lives in the game's code. Use whenever a task involves RimWorld modding, Def XML, or the game's assemblies.
---

# RimSearcher

Two sources of truth, and they answer different questions.

**The snapshot** — a database of every def the game had in memory at the moment it was exported.
Patches applied, inheritance resolved, defs generated in code included. Query it with the
`rimsearcher` CLI. One part of it is read from the mods' XML rather than from memory — the
inheritance layer, because the game throws inheritance away before the export point; `inherit`
is the only command that reads it, and it says so.

**The assemblies** — the game's compiled C#. Query it with the DecompilerServer MCP
(`mcp__decompiler__*`), which reads metadata directly.

## Pick the tool by what you are asking

| The question | Where it is answered |
|---|---|
| What does this def actually contain? | `rimsearcher get <defName>` |
| What is this thing called? I only know part of it. | `rimsearcher search <words>` |
| Which defs use this C# class / this value? | `rimsearcher find <field> <value>` |
| What can this field be set to? | `rimsearcher values <field>` |
| What fields does this def type have? | `rimsearcher fields <DefType>` |
| Everything of one kind | `rimsearcher list <DefType>` |
| What does this inherit from / what inherits from it? | `rimsearcher inherit <name>` |
| What does this method do? | `mcp__decompiler__get_decompiled_source` |
| Who calls it / what does it override / what derives from it? | `mcp__decompiler__find_callers`, `get_overrides`, `find_derived_types` |
| Where is this type? | `mcp__decompiler__search_types` |
| A code *shape* across all files, e.g. every method matching a signature pattern | `rimsearcher code-search <regex>` |
| The actual text of one file, one member, or one line range | `rimsearcher read <file> --member <name>` |

## If your instinct is to grep the XML, stop

Searching Defs XML with a regular expression was the old way, and it does not apply here.
The XML on disk is not what the game ended up with: PatchOperations rewrite it, inheritance
merges it, and thousands of defs (`Meat_*`, `Corpse_*`, `Make_*`, blueprints, frames) are
generated in code and exist in no file at all.

Translate the intent instead:

| Old habit | Now |
|---|---|
| grep `<defName>Bullet_` | `rimsearcher search Bullet_` |
| grep `<li Class="CompProperties_AmbientSound">` | `rimsearcher find compClass CompAmbientSound` |
| grep for a `<thingClass>` to see who uses it | `rimsearcher find thingClass <ClassName>` |
| grep to find what values a tag takes | `rimsearcher values <tag>` |
| grep `Name="BaseBullet"` to find the abstract parent | `rimsearcher inherit BaseBullet` |

Note the second row: the XML names the **properties** class (`CompProperties_AmbientSound`),
but the field on the def holds the **comp** class the game resolved it to (`CompAmbientSound`).
Ask for the properties name and `find` will tell you this and name the right one, but it is
one round trip you do not need to spend.

`code-search` searches **decompiled C#**, never Defs. Pointing it at data questions returns
nothing and tells you so. Its `--files` glob is matched against the path relative to the
decompiled root, so a glob with a `/` in it starts with the tree's name — `vanilla/**/Widgets.cs`,
not `Verse/Widgets.cs` — and that stays true under `--source`. A glob with no `/` matches the
file name alone at any depth, which is what `*.cs` does.

The decompiled C# is a tree on disk, one directory per mod named by its packageId (the game's
own code is `vanilla`). `rimsearcher sources list` says which trees exist and which ones came
from assemblies that have since changed; `rimsearcher sources sync` rebuilds the stale ones from
whatever the snapshot's mods actually load. A tree reported as stale still answers questions —
about the older build. Say which when it matters.

Neither command compares versions. That tree is a git repository, so **what changed between
builds is a `git diff` / `git log -p` question**, and asking it that way also gets you rename
detection and per-file history. Do not add a remote to it: it holds decompiled game code and
stays local.

## Working through a question

1. **Do not know the exact name.** `search` takes words, partial names, and translated text.
   It tolerates misspellings and CamelCase initials, and it matches inside compound names, so
   `search shield` finds `Apparel_ShieldBelt`. It covers def names, labels, descriptions and
   translations — **not C# class names**. `search CompShield` finds nothing no matter how you
   spell it; that question is `find compClass CompShield`.
   The `matched_on` column says *where* each row matched. A row with an empty `label` did not
   fail to match; it matched somewhere else, and that column names the place.
2. **Have the exact name.** `get` shows identity, every field path with its value, and any
   translations. Field paths are indexed, so `comps[4].compClass` is the real, post-patch shape.
   A big def has hundreds of paths, so name what you want rather than dumping and filtering:
   `get Apparel_ShieldBelt --path statBases`. Same switch on `fields`.
   Every field row carries a `code_default` column, and it decides how much a value is worth:
   `no` means the value differs from what a fresh instance of the declaring type carries, so
   something — XML, a patch, or `ResolveReferences` — put it there. `yes` means it is the same
   as that fresh instance, so the snapshot **cannot tell** whether anyone set it; quoting such a
   row as "this def sets X" is the single most common way to get a confident wrong answer here.
   `unknown` means the type could not be constructed for comparison, so neither claim holds.
   `yes` rows are left out of the listing by default, with a line saying how many and how to see
   them; `--defaults` lists everything, and `--path <text>` always shows a named field whichever
   kind it is.
3. **Working backwards from a class or a value.** `find` matches the field path from the end:
   `find compClass RimWorld.CompShield` needs no index and no full path.
   `values <field>` gives the whole value space, and prints which full paths and def types
   contributed — a bare name like `damageAmountBase` can match several unrelated paths, and
   that header is how you tell which ones you are actually looking at.
   When you know the value but are guessing at the field name, do not guess:
   `find --value World/WorldObjects/Expanding` searches every field and reports which paths
   hold it. Guessing a plausible field name and getting a clean, complete-looking table for
   the wrong field is the expensive failure here.
4. **A def type in `list` is a storage bucket, not a runtime class.** The game groups subclasses
   under their base's database, so `CreepJoinerAggressiveDef` instances live under
   `CreepJoinerBaseDef`. When a bucket holds more than one class, `list` adds a `class` column,
   and `--class <ClassName>` filters to one. `list <SomeClass>` tells you where to look rather
   than claiming the type does not exist.
5. **Inheritance.** Every other command answers from the objects the game had in memory, where
   inheritance is already resolved and invisible. `inherit` is the one exception: it reads the
   mods' XML, so it can show abstract parents, `ParentName` chains, and what inherits from a
   node. Two consequences. It is the XML **before** PatchOperations, and each named node reports
   how many patch operations target it by name — zero means what you see is what the game read.
   And an abstract node has no field values of its own here; everything it declares is already
   merged, post-patch, into each child, so read a concrete child with `get`.
6. **Moving into the code.** Once you have a class name from a def, hand it to
   `mcp__decompiler__search_types` and read the member you need. This is the common path:
   most def questions end in a C# question.

## Reading the output

Results are a plain table with a count above it, and the count is **always** there. Anything else
the tool needs to tell you about *this* answer is another sentence — a snapshot that no longer
matches the installed game, a boundary in the data, translations that are searchable but were not
in effect. Never read silence as a claim: two separate things can shorten a result, so the count
states its own status rather than leaving you to infer it.

Counts are written three ways, and the difference matters:

- `12 defs.` — that is all of them.
- `12 of 347 defs; pass --offset 12 for the next page…` — cut off; 347 exist.
- `at least 12 matches` — the scan stopped early; the true total is unknown.

A count of matched rows under `--path` is a filter you asked for, not a truncation; in `--json`
those carry `kind: "filter"` while a real cut-off carries `kind: "truncation"`.

`--limit all` lifts the **row** cap. `code-search` has three caps rather than one, and they
divide in two: `--limit` and `--max-per-file` decide how many matching lines are *printed*, and
neither shortens the scan, so the match count stays exact whichever of them bites; `--max-files`
decides how much is *read*, so only that one can make the answer partial. When it bites, the
count drops to `at least N`, the answer says which tree was read in part and which trees were
never reached at all, and the fix is to raise it — not to look somewhere else. A zero result
from a scan that stopped short says so and does **not** point you at the snapshot.

`code-search` also reports matches and files as two different numbers — a question about how many
methods have some shape wants the first.

Once `code-search` has told you *where*, `read` gives you the text. It takes a path from a
`code-search` hit, any tail of one, or a bare file name, and then either `--member <name>` /
`--type <name>` for one declaration or `--lines <a-b|a+n|all>` for raw lines; `--outline` lists
every declaration in the file with its line range. It finds a declaration's end by matching
braces, not by parsing C#, and says so on the paths where that inference happens. Two things it
refuses to guess at, because a wrong guess here reads exactly like a right one: when a bare file
name matches several files it lists them instead of picking, and `--lines` together with
`--member`/`--type` is a usage error rather than a silent preference. Reading a member of a
**loaded assembly** is still the decompiler MCP's job; `read` is for the decompiled tree on disk,
and it is the only way to see a specific file when that MCP is not available.

`--json` gives machine-readable output: the root is an object, every prose sentence moves into
`notes` as `{kind, text}`, and the data sits beside it under a key that depends on the command —
`defs` for `search`/`list`/`get`, `matches` for `code-search` and for `find` with a field path,
`paths` for `find --value`, `values`, `fields`, `types`, `mods`, `nodes` for `inherit`, `source`
and `declarations` for `read`. Do not guess: `<command> --help` lists that command's keys, as does
[references/cli-reference.md](references/cli-reference.md). Reading a key the command does not
produce gives you nothing, which is indistinguishable from an empty result. Code output is rows
too — `code-search` gives `{file, line, is_match, group, text}` and `read` gives `{file, line,
text}`, so nothing has to be parsed back out of `path:line:text`.

Exit codes carry four distinct meanings: `0` the command ran, `1` this query returned no rows,
`2` you used it wrong, `70` a defect in the tool. **A `1` is an answer, not a failure** —
"nothing in this snapshot has that value" is information, and the reasoning behind it is printed
on stdout either way. So chain with `;` rather than `&&`: a `1` on a query that answered your
question perfectly well would otherwise silently drop whatever you queued after it.

Do not pipe the output through `grep`. The sentence saying the result was cut short is on the
same stream as the table, so filtering it away turns "truncated" into "absent". Narrow inside
the tool instead, where the counts stay honest:

| Command | Narrow with |
|---|---|
| `get` | `--path`, `--type`, `--defaults`, `--limit` |
| `fields` | `--path`, `--offset`, `--limit` |
| `values` | `--type`, `--scope`, `--offset`, `--limit` |
| `search` | `--type`, `--scope`, `--offset`, `--limit` |
| `list` | `--class`, `--scope`, `--offset`, `--limit` |
| `inherit` | `--limit` |
| `find` | `--scope`, `--exact`, `--offset`, `--limit` |
| `code-search` | `--source`, `--files`, `--max-files`, `--max-per-file`, `--limit` |
| `read` | `--member`, `--type`, `--lines`, `--outline`, `--limit` |
| `sources sync` | `--only`, `--modlist`, `--force`, `--dry-run` |

That `head` habit has a replacement too. Every table above pages with `--offset`, and a paged
answer always states the three things a pipe would have destroyed: how many rows this page holds,
how many exist in total, and the exact `--offset` for the next page. The last page says it is the
last one rather than leaving you to do the arithmetic, and an `--offset` past the end is reported
as an overshoot, not as "nothing found". `read` pages the same way with `--lines`.

`--type <DefType>` picks one def when a name is shared — which is common: `PsychicSensitivity`
is both a `StatDef` and a `TraitDef`. `--json` keeps each of them in its own slot regardless.

If a def was truncated at export time, `get` says so on that def. When it does, a field path
missing from the list is **not** evidence that the def lacks it — raise `--limit` or trust the
warning rather than concluding the field does not exist. The same boundary applies to `find`,
`values` and `fields`, whose counts are over **indexed** paths; `rimsearcher snapshot truncated`
lists the affected defs so you can cross-check.

## Snapshots

A snapshot is one export: one game version, one ordered mod list, one language. Several can
coexist. `rimsearcher snapshot list` shows them; `--snapshot <name>` picks one for a single
command; `snapshot use <name>` makes it stick.

The snapshot is compared with the currently installed game on every query, but it only speaks up
when something is off *and* this command did not name a snapshot itself. Pass `--snapshot` and
it stays quiet, because you already said which environment you meant. `snapshot status` gives
the full comparison whenever you want it.

This matters for counts. A complete count is complete **for the snapshot**. If the snapshot
covers Core only and your game has mods enabled, `find compClass X` returning `1 def` means one
in Core, not one in your game — and the tool says so when that gap exists.

Data is as of the export. If the game or its mods have been updated since, re-export:
`rimsearcher export --modlist <name>` drives the game unattended and imports the result. It runs
the game headless, so it takes minutes on a large mod list and prints nothing while it works. If a
loading stage sits still it says so on stderr and **keeps waiting** — that line is a report, not a
verdict, and the only thing that stops the game is `--timeout`. Raise `--timeout` rather than
treating a stall report as failure.

## Parameters

`rimsearcher <command> --help` is authoritative, and
[references/cli-reference.md](references/cli-reference.md) is the same content in one page.
Unknown options are rejected rather than ignored, with the nearest accepted spelling — or, if
another command takes that option, which one — so a wrong guess costs one line, not a wrong
answer.

For the decompiler MCP, see [references/decompiler-mcp.md](references/decompiler-mcp.md).

`--scope vanilla` (also `core`, `base`, `official`) means **every module Ludeon ships** — Core
plus each DLC the snapshot covers — which is not the same as a snapshot that happens to be
*named* `vanilla`. The two look identical in a sentence, so the output spells out what a scope
resolved to whenever it is more than one mod.

## Recovering

- **Nothing found.** A zero result names its own cause: the tool checks whether the name is a
  def hidden by your `--scope`, an abstract XML parent, a def type, a class, a mod, or a def
  that lives in one of your *other* snapshots — and says which. Read that sentence before
  concluding the thing does not exist; "not here" and "not anywhere" are different answers and
  the tool distinguishes them.
- **The def exists in game but not in the snapshot.** Its mod was probably not enabled when the
  snapshot was taken. `rimsearcher mods` lists what the snapshot covers and
  `rimsearcher snapshot status` compares it with the installed game. If another registered
  snapshot has that def, the zero result says so by name.
- **You are looking for an abstract parent.** It is not a def and `get` will not find it: the
  game resolves inheritance while loading and then discards it, so an abstract
  `<ThingDef Name="…">` never becomes an object. `rimsearcher inherit <name>` answers from the
  inheritance layer instead, which is read from the mods' XML. `get` recognises a name that
  lives only there and says so rather than reporting it absent.
  For the C# side of a hierarchy, `mcp__decompiler__find_derived_types`.
- **Use text search last, not first.** `find` and `values` answer from resolved data and are
  exact; `code-search` is text and matches identically-named things from unrelated types.
