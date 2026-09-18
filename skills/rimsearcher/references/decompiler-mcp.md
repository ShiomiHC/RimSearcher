# DecompilerServer MCP

A separate program that reads the game's compiled assemblies directly. rimsearcher neither
ships it nor depends on it; whether it is installed is a fact about the environment.

Reach for it only for the three things [code-side.md](code-side.md) names — usages of a
field or a type, reading members from many files in one call, and comparing two builds. Everything
else on the code side is answered by `types` / `members` / `il` / `callers` / `read` /
`code-search`, which read one build and cost no setup.

Tool names below are the bare names; the full prefix is `mcp__decompiler__`.

## Getting started

If the tools are deferred, load them with this exact line rather than a keyword search. A
keyword search ranks by relevance and returns only part of the toolkit.

This is the one place the full prefix is written out: `select:` matches tool names exactly, so a
bare name here returns **"No matching deferred tools found"** — a zero that looks like the server
is absent rather than like a mistyped query.

```
ToolSearch select:mcp__decompiler__status,mcp__decompiler__load_assembly,mcp__decompiler__resolve_member_id,mcp__decompiler__find_usages,mcp__decompiler__batch_get_decompiled_source,mcp__decompiler__compare_symbols,mcp__decompiler__compare_contexts
```

```
load_assembly { gameDir: "<RimWorld folder>" }        # finds Assembly-CSharp.dll itself
status                                                # what is loaded, under which alias
```

A mod's own DLL loads as a second context:
`load_assembly { assemblyPath: "...\\Assemblies\\Foo.dll", contextAlias: "foo", makeCurrent: false }`.
Contexts coexist and every query takes `contextAlias`, but **one query only ever looks at one
context** — to cover the game and a mod, ask twice. This is the difference that matters against
the CLI, which reads every synced tree in one run.

## The three

- **`find_usages`** — where a field or a type is used, not just where a method is called.
- **`batch_get_decompiled_source`** — members from several files in one call, by member id. It reports
  `truncated: false` at the top level even when a slice inside it stopped short of its
  method; check `endLine` against `totalLines` per slice.
- **`compare_symbols` / `compare_contexts`** — differences between two loaded versions.

## If you use the rest anyway

- `search_types` has exactly one filter — `namespaceFilter` — plus `includeNested`. It has no
  `kind`, no `accessibility`, no `declaringType`. Passing one is not a narrower search.
  `search_members` has the rest. Both take `query`, **not** `pattern`.
- **`list_members` signatures erase generic arguments.** `IEnumerable<IGrouping<BodyPartRecord,
  Hediff>>` shows as `IEnumerable`. When the type arguments are the answer, read the member.
- **`search_members` with `mode: "signatures"` drops `declaringType`.** Six same-named members
  then look identical.
- **`search_string_literals` returning zero does not mean the literal is absent**: before the
  index is built it returns `{items: [], totalEstimate: 0}`, which is indistinguishable from a
  genuine miss. Check `stringLiteralIndexReady` in `status` first.
- **`set_decompile_settings` takes only seven switches** (`usingDeclarations`,
  `showXmlDocumentation`, `namedArguments`, `makeAssignmentExpressions`, `alwaysUseBraces`,
  `removeDeadCode`, `introduceIncrementAndDecrement`). Anything else — including the C#
  language version — is **accepted with `status: ok` and then ignored**. Read the returned
  settings back rather than trusting the call.
- A `callvirt` records the **declaring** type's method, here as anywhere: callers of an
  override miss calls made through a base-class reference.
