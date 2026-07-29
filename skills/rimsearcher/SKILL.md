---
name: rimsearcher
description: Answer questions about RimWorld's defs and C# — what a def contains after patches and inheritance, which defs use a class or a value, what a field can be set to, and where a symbol lives in the game's code. Use whenever a task involves RimWorld modding, Def XML, or the game's assemblies.
---

# RimSearcher

Two sources of truth, and they answer different questions.

**The snapshot** — a database of every def the game had in memory at the moment it was exported.
Patches applied, inheritance resolved, defs generated in code included. Query it with the
`rimsearcher` CLI.

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
| What does this method do? | `mcp__decompiler__get_decompiled_source` |
| Who calls it / what does it override / what derives from it? | `mcp__decompiler__find_callers`, `get_overrides`, `find_derived_types` |
| Where is this type? | `mcp__decompiler__search_types` |
| A code *shape* across all files, e.g. every method matching a signature pattern | `rimsearcher code-search <regex>` |

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

Note the second row: the XML names the **properties** class (`CompProperties_AmbientSound`),
but the field on the def holds the **comp** class the game resolved it to (`CompAmbientSound`).
Ask for the properties name and `find` will tell you this and name the right one, but it is
one round trip you do not need to spend.

`code-search` searches **decompiled C#**, never Defs. Pointing it at data questions returns
nothing and tells you so.

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
5. **Moving into the code.** Once you have a class name from a def, hand it to
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
- `12 of 347 defs; raise --limit.` — cut off; 347 exist.
- `at least 12 matches` — the scan stopped early; the true total is unknown.

A count of matched rows under `--path` is a filter you asked for, not a truncation; in `--json`
those carry `kind: "filter"` while a real cut-off carries `kind: "truncation"`.

`--limit all` lifts the **row** cap. `code-search` has a second, separate cap on how many files
it will read; that one is `--max-files`, and when it bites it names the source trees it never
reached. Raising `--limit` does nothing for it. `code-search` also reports matches and files as
two different numbers — a question about how many methods have some shape wants the first.

`--json` gives machine-readable output with the same prose moved into a `notes` array.

Do not pipe the output through `grep`. The sentence saying the result was cut short is on the
same stream as the table, so filtering it away turns "truncated" into "absent". Narrow inside
the tool instead, where the counts stay honest:

| Command | Narrow with |
|---|---|
| `get` | `--path`, `--type`, `--limit` |
| `fields` | `--path`, `--limit` |
| `values` | `--type`, `--scope`, `--limit` |
| `search` | `--type`, `--scope`, `--limit` |
| `list` | `--class`, `--scope`, `--offset`, `--limit` |
| `find` | `--scope`, `--exact`, `--limit` |
| `code-search` | `--source`, `--files`, `--max-files`, `--limit` |

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
`rimsearcher export --modlist <name>` drives the game unattended and imports the result.

## Parameters

`rimsearcher <command> --help` is authoritative, and
[references/cli-reference.md](references/cli-reference.md) is the same content in one page.
Unknown options are rejected rather than ignored, with the nearest accepted spelling — or, if
another command takes that option, which one — so a wrong guess costs one line, not a wrong
answer.

For the decompiler MCP, see [references/decompiler-mcp.md](references/decompiler-mcp.md).

## Recovering

- **Nothing found.** Check you are asking the right source: data questions go to the snapshot,
  code questions to the decompiler. `rimsearcher types` shows what the snapshot holds;
  `rimsearcher mods` shows which mods it covers.
- **The def exists in game but not in the snapshot.** Its mod was probably not enabled when the
  snapshot was taken. `rimsearcher mods` lists what the snapshot covers and
  `rimsearcher snapshot status` compares it with the installed game.
- **You are looking for an abstract parent, and it is not there.** Abstract `<ThingDef Name="…">`
  nodes and `ParentName` links exist only while the game is loading XML; the game clears them
  before the export point, so **no snapshot has ever held them**. Their fields are not lost —
  they are already merged into every child. Ask a child def with `get`, or use
  `mcp__decompiler__find_derived_types` for the C# side of the hierarchy.
- **Use text search last, not first.** `find` and `values` answer from resolved data and are
  exact; `code-search` is text and matches identically-named things from unrelated types.
