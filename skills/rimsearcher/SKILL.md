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
| Which layer of the chain writes this field? | `rimsearcher inherit <def> --path <field>` |
| What does this interface text say, or which key is behind it? | `rimsearcher keyed <key or phrase>` |
| What does this method do? | `mcp__decompiler__get_decompiled_source` |
| Who calls it / what does it override / what derives from it? | `mcp__decompiler__find_callers`, `get_overrides`, `find_derived_types` |
| Where is this type? | `mcp__decompiler__search_types` |
| A code *shape* across all files, e.g. every method matching a signature pattern | `rimsearcher code-search <regex>` |
| The actual text of one file, one member, or one line range | `rimsearcher read <file> --member <name>` |

The MCP is often not connected — that is a normal state, not an error. Every row above that
names it has a CLI answer, and these are the ones to reach for:

| Instead of | Without the MCP |
|---|---|
| `get_decompiled_source` | `rimsearcher read <File>.cs --member <name>` |
| `search_types` | `rimsearcher code-search "class <Name>\b"`, then `read` that file `--outline` |
| `find_derived_types` | `rimsearcher code-search "class \w+ : <Base>\b"` |
| `get_overrides` | `rimsearcher code-search "override [\w<>, \[\]]+ <Member>\("` |
| `find_callers` | nothing exact. `code-search` matches by *text*, so same-named members of unrelated types land in the same result set. Read the hits; do not report their count as a caller count. |
| `get_il` | **nothing at all.** The CLI has no IL view. Opcode-level questions — `Call` vs `Callvirt`, what a Harmony transpiler is matching, a lambda that the compiler moved into a generated closure method — are invisible in decompiled C# text and cannot be answered without the MCP. Say so rather than inferring from the C#. |

Note what the first four have in common: **anchor the pattern, do not search a bare symbol name.**
`code-search MapPortal` returns every mention of it — hundreds of lines on a common name, and
the declaration is not marked among them. `code-search "class MapPortal\b"` returns exactly one.
Once you have the file, `read <file> --outline` lists what is in it without a second scan; that
is the right move whenever the only hit was the declaration itself, because a single hit means
the pattern found *where the thing is defined*, not *what it does*.

## What is out of range

Two questions the tool cannot answer, and it is cheaper to know than to discover:

- **"How should this XML be written?"** The snapshot holds runtime objects, not the XML that
  produced them, so there is no path from a def back to authorable source. `inherit` is the one
  place XML is read at all, and it reads the *inheritance* layer only. For the shape of a tag,
  read the declaring C# class (`read <Class>.cs --outline`) — field names and types are the same
  ones the XML parser binds to. `get` does print a `source` line, and it is less than it looks:
  the bare file name the game reported, no directory, unverified. It narrows the search inside
  that mod's Defs folder; it does not open anything.
- **Files on disk.** Texture and sound *paths* are indexed because they are def fields
  (`values texPath` works), but nothing here reads the file system to say whether
  `Things/Item/Foo.png` exists, what resolution it is, or what else sits in that folder. That is
  an `ls` question.

## Text on screen

Most of what a player reads is not a def. Button captions, alerts, tooltips and failure reasons
are **keyed translations** — `"CannotUseNoPower".Translate()` — and they belong to no def at all,
which is why no amount of `search`, `get` or `find` reaches them. `rimsearcher keyed` is the one
that does, in both directions: given a key it shows what the game displays, and given a phrase in
either language it shows which keys carry that text.

That is the road from a line on screen to the code that prints it — take the key and run
`rimsearcher code-search '"TheKey"'`. Going the other way needs no second step:
`code-search` resolves every key written as a literal on a matching line and prints a `ui_text`
table beside the hits, so a scan through `.Translate()` call sites already says what each one
displays. `--no-ui-text` turns that off. A key the code assembles at runtime (`"Stat_" + x`) has
no literal to resolve, and the answer says how many lines were like that rather than leaving them
blank.

Rows carry an `origin` of `in effect` or `on disk`, and only `in effect` is what the game displays:
keyed translations override each other by mod load order, the snapshot keeps the winner, and an
`on disk` row is a translation some mod's language files contain without necessarily being the one
that wins. A `placeholder` row is a key whose language file declares it without a translation, so
the game falls back to English there — that is what a translation-coverage question is asking
about, and `--placeholders` lists only those.

One boundary is the snapshot's rather than any query's: this layer is written by the in-game
exporter, so if the exporting game had no language data loaded there are no keyed translations at
all. `keyed` says that in those words instead of reporting your key absent.

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
   `search shield` finds `Apparel_ShieldBelt`. It covers def names, labels, descriptions and the
   translations injected onto defs — **not C# class names**, and **not the UI strings under
   `Languages/*/Keyed`**. `search CompShield` finds nothing no matter how you spell it; that
   question is `find compClass CompShield`; a phrase off the screen is `keyed <phrase>`, and a
   zero result here names whichever of the two it turns out to be.
   Both sides of a translation are searchable, so **an English term still finds its def on a
   Chinese snapshot** — `search "brain damage"` works even when every label in the snapshot is
   Chinese.
   The `matched_on` column says *where* each row matched. A row with an empty `label` did not
   fail to match; it matched somewhere else, and that column names the place.
2. **Have the exact name.** `get` shows identity, every field path with its value, and any
   translations. Field paths are indexed, so `comps[4].compClass` is the real, post-patch shape.
   A big def has hundreds of paths, so name what you want rather than dumping and filtering:
   `get Apparel_ShieldBelt --path statBases`. Same switch on `fields`.
   `--path` and `--value` match as **substrings**, which is why a partial name works at all — and
   also why `--path soundImpact` returns `soundImpactDefault`, a field with the opposite meaning.
   The output says when nothing matched as a whole path segment, so read that line before treating
   a single clean row as the field you asked for.
   Fields inside one indexed block constrain each other, and a `--path` filter cuts the siblings
   away: the output names any hand-set field in the same `comps[N]` block as the rows it printed.
   That line is not decoration — `minFuelCost` and `fuelPerTile` sit in one block and answer
   different halves of "how far does this go".
   Every field row carries a `code_default` column, and it decides how much a value is worth:
   `no` means the value differs from what a fresh instance of the declaring type carries, so
   something — XML, a patch, or `ResolveReferences` — put it there. `yes` means it is the same
   as that fresh instance, so the snapshot **cannot tell** whether anyone set it; quoting such a
   row as "this def sets X" is the single most common way to get a confident wrong answer here.
   `unknown` means the type could not be constructed for comparison, so neither claim holds.
   `yes` rows are left out of the listing by default, with a line saying how many and how to see
   them; `--defaults` lists the rest of the indexed paths, and `--path <text>` always shows a
   named field whichever kind it is.
   Read the column for what it is: it answers **who set this value**, never **what the value is**.
   The value in the row is the real one either way, so a rule that reads the value — a property
   like `HarvestDestroys => harvestAfterGrowth <= 0f`, a threshold, a comparison — is answerable
   from a `yes` row, and answering "cannot tell" there throws away a correct result. The same
   goes the other way for discriminator fields: `compClass`, `thingClass` and `workerClass` are
   assigned in their own type's constructor, so `yes` is their normal and correct state, not a
   warning.
   One thing neither switch reaches: a field whose value was null never entered the index at all,
   so it is absent from `--defaults` too. Its absence is not evidence that the field does not
   exist — read the declaring class to see the shape.
3. **Working backwards from a class or a value.** `find` matches the field path from the end:
   `find compClass RimWorld.CompShield` needs no index and no full path.
   The runtime type of a nested `<li Class="…">` is queryable the same way, under the field name
   `Class`: `find Class RimWorld.CompProperties_Shield` is how you ask which defs carry that
   node. This is one dimension the index only started measuring at exporter 0.2.0, and on an
   older snapshot the query says so rather than returning a bare zero — if you see that line,
   re-export before reading the zero as an answer.
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
   node. Two consequences. It is the XML **before** PatchOperations, and each node that declares
   `Name=` reports how many patch operations target it by name — zero means what you see is what
   the game read. A node without a `Name=` reports `n/a`, not zero: patches that reach a def by
   its defName are counted nowhere, so for those defs "has a mod patched it?" stays unanswered.
   And an abstract node has no field values of its own here; everything it declares is already
   merged, post-patch, into each child, so read a concrete child with `get`.
6. **Moving into the code.** Once you have a class name from a def, hand it to
   `mcp__decompiler__search_types` and read the member you need — or, with no MCP,
   `code-search "class <Name>\b"` to land on the file and `read <file> --member <name>` for the
   body. This is the common path: most def questions end in a C# question.
   Two things the decompiled text has already lost, so no pattern can find them: **comments**
   (only ILSpy's own notes about what it could not translate survive) and **local variable
   names** (re-invented from the assignment as `num`, `num2`, `list`, `flag`). Parameter and
   member names do survive. A member you expect on a class and cannot find is usually inherited —
   the decompiler does not repeat a base type's members, so follow the `: Base` on the class line.

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
`--member`/`--type` is a usage error rather than a silent preference. **Give it the bare file
name; do not build a path out of the namespace.** The tree's folders are not namespaces —
`HealthCardUtility` sits under `RimWorld/` and `HealthUtility` under `Verse/` — so a guessed
`RimWorld/HealthUtility.cs` misses a file that `HealthUtility.cs` finds at once. Reading a member of a
**loaded assembly** is still the decompiler MCP's job; `read` is for the decompiled tree on disk,
and it is the only way to see a specific file when that MCP is not available.

`--json` gives machine-readable output: the root is an object, every prose sentence moves into
`notes` as `{kind, text}`, and the data sits beside it under a key that depends on the command —
`defs` for `search`/`list`/`get`, `matches` for `code-search` and for `find` with a field path,
`paths` for `find --value`, `nodes` for `inherit`, `keys` for `keyed`, `source` and `declarations`
for `read`, and a key named after the command itself for `values`, `fields`, `types` and `mods`.
Two commands carry a second key: `values` has `field`, saying which full paths and def types its
value space was drawn from, and `code-search` has `ui_text`, holding the keyed translation of every
key written as a literal on a matching line.
Do not guess: `<command> --help` lists that command's keys, as does
[references/cli-reference.md](references/cli-reference.md). Reading a key the command does not
produce gives you nothing, which is indistinguishable from an empty result. The key the command
does produce is always there, empty array and all — a missing key means you asked for the wrong
key, never that the query came up empty. Code output is rows
too — `code-search` gives `{file, line, is_match, group, text}` and `read` gives `{file, line,
text}`, so nothing has to be parsed back out of `path:line:text`.

Exit codes carry four distinct meanings: `0` the command ran, `1` this query returned no rows,
`2` you used it wrong, `70` a defect in the tool. **A `1` is an answer, not a failure** —
"nothing in this snapshot has that value" is information, and the reasoning behind it is printed
on stdout either way. So chain with `;` rather than `&&`: a `1` on a query that answered your
question perfectly well would otherwise silently drop whatever you queued after it. Note what
`;` costs you in return: the chained call reports one exit code, the last one, so a harness that
reads exit codes may wrap two perfectly good answers in an error. The output is on stdout
regardless — read it, not the code.

On PowerShell, quote regular expressions with **single** quotes. Double quotes make the shell
eat the backslashes first, and `code-search "class \w+"` then searches for `class w+`, which
matches nothing and looks exactly like an honest zero result.

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
| `inherit` | `--path`, `--limit` |
| `keyed` | `--placeholders`, `--offset`, `--limit` |
| `find` | `--scope`, `--exact`, `--offset`, `--limit` |
| `code-search` | `--source`, `--files`, `--max-files`, `--max-per-file`, `--limit` |
| `read` | `--member`, `--type`, `--lines`, `--outline`, `--limit` |
| `sources sync` | `--only`, `--modlist`, `--force`, `--dry-run` |

That `head` habit has a replacement too. `search`, `find`, `list`, `fields`, `values` and `keyed`
page with `--offset`, and a paged answer always states the three things a pipe would have
destroyed: how many rows this page holds, how many exist in total, and the exact `--offset` for
the next page. The last page says it is the
last one rather than leaving you to do the arithmetic, and an `--offset` past the end is reported
as an overshoot, not as "nothing found". `read` pages the same way with `--lines`. The rest do not
page and do not take `--offset`: `get` and `inherit` narrow with `--path` or open up with
`--limit all`, and `code-search` has its own three caps — passing `--offset` to any of them is a
usage error naming the commands that do take it, not a silently ignored switch.

`--type <DefType>` picks one def when a name is shared — which is common: `PsychicSensitivity`
is both a `StatDef` and a `TraitDef`. `--json` keeps each of them in its own slot regardless.

If a def was truncated at export time, `get` says so on that def. When it does, a field path
missing from the list is **not** evidence that the def lacks it — raise `--limit` or trust the
warning rather than concluding the field does not exist. The same boundary applies to `find`,
`values` and `fields`, whose counts are over **indexed** paths; `rimsearcher snapshot truncated`
lists the affected defs so you can cross-check, and takes `--type` and `--def` to narrow to the
ones a particular answer depended on. The footnote on such an answer prints that command already
filled in.

## Snapshots

A snapshot is one export: one game version, one ordered mod list, one language. Several can
coexist. `rimsearcher snapshot list` shows them; `--snapshot <name>` picks one for a single
command; `snapshot use <name>` makes it stick.

The snapshot is compared with the currently installed game on every query, but it only speaks up
when something is off *and* this command did not name a snapshot itself. Pass `--snapshot` and
it stays quiet, because you already said which environment you meant. `snapshot status` gives
the full comparison whenever you want it.

That comparison covers three things and no more: same mods, same order, same version. It is a
check on the *load list*, not on the files. Nothing inside those mods is compared, so a mod's
XML, patches, textures or audio can have been edited since the export and `snapshot status`
will still say the snapshot matches. If the question is "did my edit take effect", the answer
is never in that line — re-export, then ask.

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

The global options (`--snapshot`, `--db`, `--json`, `--config`) go **after** the command name:
`rimsearcher types --json`, not `rimsearcher --json types`. "Global" describes which commands
take them, not where they sit.

For the decompiler MCP, see [references/decompiler-mcp.md](references/decompiler-mcp.md).

`--scope vanilla` (also `core`, `base`, `official`) means **every module Ludeon ships** — Core
plus each DLC the snapshot covers — which is not the same as a snapshot that happens to be
*named* `vanilla`. The two look identical in a sentence, so the output spells out what a scope
resolved to whenever the expansion is not word for word what you typed.

## Recovering

- **Nothing found.** A zero result names its own cause: the tool checks whether the name is a
  def hidden by your `--scope`, an abstract XML parent, a def type, a class, a field value, a mod,
  a piece of interface text, or a def that lives in one of your *other* snapshots — and says
  which. Read that sentence before
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
- **You want to know which layer a value came from.** `get` gives the merged value and the
  inheritance layer has no field table of its own, so neither command answers it head-on — the
  snapshot does not record where a field was declared. `rimsearcher inherit <def> --path <field>`
  computes the evidence instead: for each layer in the chain it counts the *other* defs
  descending from it, how many carry that field, and how many carry the same value. A layer whose
  `with_path` falls short of `other_defs` is not the one declaring it. The reverse does not
  follow — every descendant writing the field separately looks the same — which is what the
  `same_value` column tells apart.
- **Use text search last, not first.** `find` and `values` answer from resolved data and are
  exact; `code-search` is text and matches identically-named things from unrelated types.
