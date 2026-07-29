# DecompilerServer MCP

Reads the game's compiled assemblies directly. No pre-processing step: `load_assembly` on the
game directory takes well under a second and warms its own index.

Tool names below are the bare names; the full prefix is `mcp__decompiler__`.

## Getting started

```
load_assembly { gameDir: "<RimWorld folder>" }        # finds Assembly-CSharp.dll itself
status / list_contexts                                # what is loaded, under which alias
```

A mod's own DLL loads as a second context:
`load_assembly { assemblyPath: "...\\Assemblies\\Foo.dll", contextAlias: "foo", makeCurrent: false }`.
Contexts coexist and every query takes `contextAlias`, but **one query only ever looks at one
context** — to cover the game and a mod, ask twice.

## Finding a symbol

| Want | Tool |
|---|---|
| Type by name or fragment | `search_types` (`query`, `regex`, filters on namespace/kind/accessibility) |
| Member by name | `search_members` (same shape) |
| Either | `search_symbols` |
| A fully-qualified name you already believe | `resolve_member_id` |
| What is in a namespace | `get_types_in_namespace`, `list_namespaces` |

`search_types` and `search_members` take `query`, **not** `pattern`, and support `regex: true`
plus filters (`kind`, `namespace`, `declaringType`, `accessibility`, `isStatic`, `genericArity`).
That covers most of what you would otherwise reach for a text search to do.

## Reading

- `list_members` / `get_members_of_type` — the outline. Do this before guessing a method name.
- `get_decompiled_source` — one member or one type, as C#.
- `batch_get_decompiled_source` — several at once.
- `plan_chunking` + `get_source_slice` — for something too large to read whole.
- `get_member_signature`, `get_overloads`, `get_xml_doc`, `get_ast_outline`.

## Relationships

All of these read metadata, so they are exact rather than textual:

- `find_derived_types` — pass `transitive: true` for the whole subtree, not just direct children.
- `find_base_types`, `get_overrides`, `get_implementations`
- `find_callers`, `find_callees`, `find_usages`

One inherent limit, and it is not specific to this server: a `callvirt` records the **declaring**
type's method. So looking up callers of an override misses calls made through a base-class
reference, and looking up callers of the base method finds them without telling you which
override actually ran. Cross-check with `get_overrides` / `find_derived_types` when it matters.

## Harmony

- `suggest_transpiler_targets`, `generate_harmony_patch_skeleton`, `generate_detour_stub`,
  `generate_extension_method_wrapper`

`get_il` is needed for **transpilers only**. A transpiler matches the instruction sequence, and
the decompiler deliberately erases it — iterator state machines become `yield`, closures become
lambdas, switch jump tables become `switch`. For a prefix or a postfix, read the decompiled C#.

## Other

- `search_string_literals` — regex over IL string literals. Finds reflection targets and
  `Translate()` keys that no symbol-level query would.
- `compare_symbols` / `compare_contexts` — differences between two loaded versions.
- `set_decompile_settings` — **only seven switches**
  (`usingDeclarations`, `showXmlDocumentation`, `namedArguments`, `makeAssignmentExpressions`,
  `alwaysUseBraces`, `removeDeadCode`, `introduceIncrementAndDecrement`).
  Anything else — including the C# language version — is **accepted with `status: ok` and then
  ignored**. Read the returned settings back and check rather than trusting the call.
- `warm_index`, `clear_caches`, `unload`, `get_server_stats`

## What it does not do

Match an arbitrary regular expression against **method bodies as text**. `search_string_literals`
covers literals only. For a shape like `public\s+(?:virtual\s+)?void\s+Notify_\w+\(` across every
file, use `rimsearcher code-search`.
