# rimsearcher usage notes

SKILL.md holds the contracts; this page holds the mechanics, edges, and worked examples
behind them. Nothing here overrides SKILL.md — it explains it.
[cli-reference.md](cli-reference.md) (generated, authoritative) lists every command and option.

## Snapshot management

**What `snapshot status` compares.** Four things: same mods, same order, same game build,
and — for snapshots exported once this was measured — the size and timestamp of every XML
file under `Defs/` and `Patches/` of each mod it could locate on disk. The `xml_fingerprint`
row says how many mods that covers, which is usually fewer than the snapshot holds. That last
one catches a mod whose contents changed without its `About.xml` version moving, which is the
ordinary shape of a Steam workshop update. It reports which mods moved, by name.

**The fingerprint's edges** — the output states most of them, but only beside a verdict that
everything compared matches: size and timestamp are not file contents, so a re-download of
identical bytes reads as a change, and an edit that preserves both is the one case it misses.
`Languages/`, textures and audio are outside it entirely, as is any directory the installed
game version does not load (a mod's `1.5/` folder while you are on 1.6) — that last exclusion
the output never mentions. A snapshot exported before the fingerprint existed shows
`xml_fingerprint` as `not recorded (exported before this was measured)`, and — when that
snapshot matches the game on everything else compared — says in words that the line reading
"matches" is not evidence about those files. Re-export, or re-run
`rimsearcher snapshot import` on the export file, to start recording that layer.

**Where the game version comes from.** `Assembly-CSharp.dll` when `game_dir` is configured.
Without it the number falls back to `ModsConfig.xml`, which the game only rewrites when you
save a change on its mod list page — so a Steam update within the same 1.x line will not
move it. `snapshot status` names the source only in the fallback case: a note says the number
came from `ModsConfig.xml`, and silence means the DLL was read.

**What a query will and will not warn about.** Every query compares the snapshot with the
installed game, and reports only what makes the snapshot's own data wrong: the game build
moved, the `Defs/`–`Patches/` XML of the mods it describes changed on disk or went missing,
or those mods sit in a different load order now (load order decides which patch wins, so a
value can be wrong rather than merely partial). Which mods the game happens to have enabled
is **never** reported — enabling five more mods, or disabling one, leaves every query
silent. That is an environment choice, not staleness: a snapshot deliberately narrowed to
Core plus the official DLCs would otherwise carry a warning that is true forever and fixable
never. Ask `snapshot status` for those numbers, and note that a zero result already names
another snapshot that holds the def you asked for. No selector mutes any of this —
`--snapshot <name>` says which environment you meant, not that you knew it had moved.

**The changed-XML line moves, so do not read only the header.** It sits at the top when the
answer names one of the changed mods, when there is no answer at all, or when the output has
no `mod` column to rule it out (`fields`, `values`, `code-search`). It drops *below* the
table when every row provably comes from other mods. It is repositioned, never suppressed:
a row missing because a changed mod deleted it looks exactly like a zero result, which is
why zero results keep it at the top. The other three lines — build moved, load order
reshuffled, mod list unreadable — always stay at the top.

**`modlist show --find <text>`** searches every saved list it can open, naming any it could
not. It answers **which saved lists name a mod** — not whether the mod is installed. This
command reads only the saved `.rml` files, never the game's mod folder, and a zero result
says so in those words. Other commands do read that folder: the XML fingerprint behind
`snapshot status`, and behind the staleness line on every query, is a scan of it.

## `--json` data keys per command

The root is an object; every prose sentence moves into `notes` as `{kind, text}` — except the
text form's "same in every row" preamble, whose value is repeated on each row instead; the
data sits under a key that depends on the command. `<command> --help` lists each command's keys.

| Command | Data key(s) |
|---|---|
| `search`, `get` | `defs` |
| `list` | `defs` (with a def type) or `types` (without) — never both |
| `find` with a field path | `matches` |
| `find --value` | `paths` |
| `values` | `values`, plus `field` (which full paths and def types the value space was drawn from) whenever the path matched something — absent when nothing did |
| `fields` | `fields` |
| `mods` | `mods` |
| `inherit` | `nodes` |
| `keyed` | `keys` |
| `code-search` | `matches`, plus `ui_text` when a printed matching line calls `.Translate()` on a literal key the snapshot can resolve |
| `read` | `source`, or `declarations` with `--outline` — never both |

Code output is rows too, so nothing is parsed back out of `path:line:text`:
`code-search` rows are `{file, line, is_match, group, text}`, `read` rows are
`{file, line, text}` — `{file, line, kind, declaration, text}` when the lines came from
`--member` or `--type`. Under `--outline` the rows are `{kind, name, in, lines, at}` instead,
and the last of those is a string like `"817-951"` to hand straight back to `--lines`, not a
pair of integers. `--json` never folds columns: every row carries every column.
Truncation notes carry `kind: "truncation"`. On the def-side commands that take `--path`
(`get`, `inherit`, `fields`), the count of rows the filter matched carries `kind: "filter"` —
a filter you asked for, not a cut-off; the code commands narrow files with `--files` instead.

## keyed: the layers in depth

The `in effect` row is listed first; keyed translations override each other by mod load
order and the snapshot keeps the winner. An `on disk` row is a translation some mod's
language files contain without necessarily being the one that wins — most come from mods
that are installed but not enabled, so they never entered the race. `placeholder` is a
separate yes/no column rather than a third origin: a row marked `placeholder=yes` declares
the key in its language file without a translation, so the game falls back to English there —
that is what a translation-coverage question is asking about, and `--placeholders` lists only
those. Such a row still carries an origin of `in effect` or `on disk`.

The `on disk` layer is scanned at import time whenever `mod_roots` is configured, so a key
that only some unenabled mod translates is still findable by its text. A snapshot built
without that scan says so beside the table rather than letting the missing layer read as an
empty one. And if the
exporting game had no language data loaded at all, there are no keyed translations in the
snapshot — `keyed` says that in those words instead of reporting your key absent.

`--placeholders` needs no query — on its own it filters the whole layer, so
`rimsearcher keyed --placeholders --limit all` is "list every untranslated string"; given a
query it narrows that result set instead. Leaving the query out *without* the switch
enumerates the layer itself, paged like any other listing. When nothing is a placeholder the
answer is a coverage statement over the whole layer; the exit code is still `1` because no
rows were printed, which is not a failed lookup.

Going from code to screen text needs no second step: `code-search` resolves the literal keys
passed to `.Translate()` on the lines it prints and appends a `ui_text` table beside the hits
(`--no-ui-text` turns it off) — a `--limit` that truncates the hits truncates that table with
it, and a literal not passed to `.Translate()` gets no row. A key the code assembles at
runtime (`"Stat_" + x`) has no literal to resolve, and the answer says how many lines were
like that rather than leaving them blank. Going from a key to the code that prints it:
`rimsearcher code-search '"TheKey"'`.

## Decompiled source trees

The decompiled C# is a tree on disk, one directory per mod **that ships an assembly**, named
by its lowercased **packageId** — the game's own code and the official DLCs are folded into a
single `vanilla` tree. `rimsearcher sources list` says which trees exist and which
came from assemblies that have since changed; `rimsearcher sources sync` rebuilds the stale
ones from whatever the snapshot's mods actually load. A tree reported as stale still answers
questions — about the older build. Say which when it matters.

When a narrowed `code-search` reports reading `23 of 33 source trees on disk`, the line names
its own cause: under `--files` the other trees hold no file that matches — never-decompiled
ones among them — and under `--source` they are the trees you excluded. An unfiltered run
prints no such fraction at all. `sources list` says which are which; a tree it lists as
`empty` and outside the snapshot is one `sources sync` plans no work for. A zero result from
a scan that stopped short says so and does **not** point you at the snapshot.

Neither command compares versions. The tree is a **git repository**, so what changed between
builds is a `git diff` / `git log -p` question — which also buys rename detection and
per-file history. Do not add a remote to it: it holds decompiled game code and stays local.

## Worked examples and derivations

**Why you never guess a class from a defName.** Across everything Ludeon ships, 98 of the 167
`GenStepDef`s have a class whose name is not the defName, and in Core alone it is 12 of 42:
`RocksFromGrid` (Core) does run `GenStep_RocksFromGrid`, but `AncientExostriderRemains`
(Biotech) runs `GenStep_ScatterLayout` and `AncientJunkClusters` (Ideology) runs
`GenStep_ScatterGroup`. A guess that lands looks exactly like one that does not, and
`code-search "class GenStep_<defName>"` returning nothing is evidence about the name you
invented, not about the def. Class names come out of `get`'s `*Class` rows: the `class` line
in the identity block is the def's **own** type (usually the same for every def of that
type), while `*.Class` and `*Class` rows — `genStep.Class`, `comps[0].compClass`,
`thingClass` — are what actually runs.

**How the `find Class` dimension arrived.** The runtime type of a nested `Class="…"` object
is queryable under the field name `Class`. It arrived in two exporter steps — list elements
(`<li Class="…">`) at 0.2.0, single class-picking fields (`GenStepDef.genStep`,
`ThinkTreeDef.thinkRoot`) at 0.4.0. A zero result ends with a line stating how far the
snapshot's exporter got: on a current snapshot, that both list elements and single fields are
indexed under `<path>.Class`; on an older one, which of the two steps is missing. On a
pre-0.4 snapshot, `find Class` and `list --class` are both structurally blind to single
class-picking fields and only `code-search` can answer; re-export to close the gap.

**Anchoring, walked through.** `code-search MapPortal` finds every mention — 81 matches in 39
files, of which `--limit` prints the first 25, the declaration rendered exactly like every
other hit. `code-search "class MapPortal\b"` returns exactly one. Once you have the file,
`read <file> --outline` lists what is in it without a second scan; that is the right move
whenever the only hit was the declaration itself, because a single hit means the pattern
found *where the thing is defined*, not *what it does*.

**What `search` tolerates.** Words, partial names, translated text, matches inside compound
names — `search shield` finds `Apparel_ShieldBelt`. Both sides of a translation are
searchable, so an English term still finds its def on a Chinese snapshot: `search "brain
damage"` works when every label in the snapshot is Chinese. When nothing matches as written
it falls back to the closest names by spelling rather than returning zero, which is what
makes a misspelling (`shild belt`) or CamelCase initials (`psl` → `PsychicShockLanceCast`)
land — those rows say so, and carry `matched_on=closest spelling`, so a fallback row is not
the def you asked for. The `matched_on` column says *where* each row matched — a row with an
empty `label` did not fail to match; it matched somewhere else, and that column names the
place.

**`code_default` exemption, concretely.** A property like
`HarvestDestroys => harvestAfterGrowth <= 0f` reads the field's value; the value in a `yes`
row is the real one, so the property's outcome is answerable from it — answering "cannot
tell" there throws away a correct result.

**Shared names across def types.** `PsychicSensitivity` is both a `StatDef` and a
`TraitDef`; `--type <DefType>` picks one, and `--json` keeps each in its own slot regardless.

**Sibling fields in one indexed block.** `minFuelCost` and `fuelPerTile` sit in one
`comps[N]` block and answer different halves of "how far does this go". A `--path` filter
cuts the siblings away, which is why the output names any hand-set field in the same block
as the rows it printed.

**Folders are not namespaces.** `HealthCardUtility` sits under `RimWorld/` and
`HealthUtility` under `Verse/`. A guessed `RimWorld/HealthUtility.cs` still reads the file —
`read` falls back to the one file with that basename and says in words that the path as
written does not exist — but the bare name `HealthUtility.cs` gets there without the detour.

**`values` on a bare field name.** `values <field>` gives the whole value space and prints
which full paths and def types contributed — a bare name like `damageAmountBase` can match
several unrelated paths, and that header is how you tell which you are looking at.

**Storage buckets.** The game groups subclasses under their base's database, so
`CreepJoinerAggressiveDef` instances live under `CreepJoinerBaseDef`. `list <SomeClass>`
tells you where to look rather than claiming the type does not exist. Most buckets hold
exactly one class; there `--class` narrows nothing, and such a def type keeps its whole
behaviour on a nested `Class="…"` field instead — every `GenStepDef` in a snapshot is a
`Verse.GenStepDef` (the count moves with the mod list: 167 in vanilla, 169 in a modded
snapshot), and which `GenStep` each runs is on `genStep`, indexed as `genStep.Class`.

## Paging and errors, in detail

`--limit all` lifts the row cap. A paged answer states the three things a pipe would have
destroyed: how many rows this page holds, how many exist in total, and the exact `--offset`
for the next page. The last page
says it is the last one; an `--offset` past the end is reported as an overshoot, not as
"nothing found". Passing `--offset` to `get`, `inherit` or `code-search` is a usage error
that names the first few commands which do take it and counts the rest, not a silently
ignored switch.

Unknown options are rejected rather than ignored, with the nearest accepted spelling — or,
if another command takes that option, which one — so a wrong guess costs one line, not a
wrong answer.

Two things `read` refuses to guess at, because a wrong guess reads exactly like a right one:
when a bare file name matches several files it lists them instead of picking, and `--lines`
together with `--member`/`--type` is a usage error rather than a silent preference.

Where a `find` guess misses: given a single word that is not a field path, `find` works out
what that word actually is — a field value, a def name, a def type, a mod — and names the
query that reaches it. Where nothing refers to that name, it says so instead of handing back
a query that would come back empty.

## Export mechanics

`rimsearcher export --modlist <name>` runs the game headless, so it takes minutes on a large
mod list and prints nothing while it works. If a loading stage sits still it says so on
stderr and **keeps waiting** — that line is a report, not a verdict, and the only thing that
stops the game is `--timeout`. (Outside export, stderr stays empty unless the command exits
`2` on a usage error: a genuine miss, or an `--offset` past the end, reports on stdout — see
the exit-code contract in SKILL.md.) Raise `--timeout` rather than treating a stall report as
failure. The `<name>` is required, and `rimsearcher modlist list` is where it comes from:
saved lists are the only thing an export can run against. One saved from the game's mod
screen, one written by `modlist save`, and one typed by hand all appear in `modlist list` and
read back the same way — a hand-written file carries no display names, which `modlist show`
states rather than reporting anything missing.
