# Snapshots: making them, and when to remake one

Picking a snapshot to query is on the main page. This page is the rest: how one is
produced, how generations of the same name rotate, and the limits of the staleness
check. None of it is needed to query data that is already exported.

## How one is made

`rimsearcher export --modlist <name>` **runs the game
headless** — launches RimWorld windowless, loads the modlist, dumps every def in
memory, exits — hence "in memory at export time". A stall report on stderr means 120s
without progress in a stage; the game **keeps running** after it, and nothing stops it
before the 900s default, which only
`rimsearcher export --modlist <name> --timeout 1800` raises. Interrupting is the one thing
that ends the run early.

## Generations, and when a re-export replaces the file

Re-exporting the same name rotates the old file to `<name>.prev`, the one before it to `<name>.prev2`, and so on; `snapshot_keep`
in the config file, or `--keep <n>`, says how many generations that name holds, counting
the one being written, and whatever falls past that count is deleted and said so.
A re-export leaves both files alone only when the exporter version, the patch route, the
resolved defs and fields **and** how many XML lines were indexed all match — an exporter
that gained an XML layer replaces the file even though no def moved. Queries raise
staleness themselves when they detect it — but the check is size and timestamp, so an edit
preserving both, or anything under `Languages/`, passes unseen. Re-export before concluding
the tool is wrong: `rimsearcher export --modlist <name>`, where `<name>` is required and
comes from `rimsearcher modlist list`.
