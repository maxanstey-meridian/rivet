# Limitations

Verified limits of the current tool, so you don't discover them in production.

## Runtime

- Rivet does not perform JSON Schema validation at runtime. Constraint attributes
  (`[Range]`, ...) are enforced by the host framework, not Rivet
  ([recipes](/guides/runtime-validation#enforcing-constraints-at-runtime);
  `[RivetConstraints]` is a `ValidationAttribute` and participates).
- Contract-owned `Success`, `Error`, and `File` terminals validate declared
  statuses, body presence, payload CLR types, derived-instance extra-field
  leakage, and declared content types before first-party MVC or Minimal API
  adaptation. They do not inspect serialized JSON against its schema. Full
  statement: [Runtime Validation](/guides/runtime-validation).
- Host effects such as authentication challenges, sign-out cookies, and dynamic
  response headers happen through ordinary ASP.NET APIs before returning a
  matching bodyless terminal. Rivet preserves an already-started matching
  response and rejects status conflicts; native framework results never pass
  through the contract.
- A body that's produced as it's sent (a live transcode) is still a Rivet file:
  `.File(writer, ...)` takes a `Func<Stream, Task>`, checked before the writer
  runs. It can't serve ranges, and once the writer has sent bytes the status is on
  the wire: an exception after that can only cut the response short.

## Generation

- Input must compile: generation aborts on compilation errors, and on type-name
  collisions between namespaces (component names are global).
- Enums are emitted as integers by default, matching ordinary System.Text.Json
  serialization of unannotated enums. String-valued enums require a type-level
  `[JsonConverter(typeof(JsonStringEnumConverter<T>))]`; Rivet reads only explicit
  type-level serializer metadata, not application-wide serializer options, so if
  your API registers that converter globally the spec will not match the wire.
- `TimeSpan` and `BigInteger` have no schema mapping — they emit an untyped (empty)
  schema with a diagnostic (`RIV1009`/`RIV1010`). Escape hatch: expose the value as a
  `string` property (ISO 8601 for `TimeSpan`, digits for `BigInteger`) or as a number
  when the range allows.
- Security scheme definitions come from the `--security` flag; `.Secure("name")`
  with no matching definition fails generation (`RIV2002`).
- The spec reflects *declared* C# types and the default System.Text.Json
  conventions. `[JsonPolymorphic]`/`[JsonDerivedType]` hierarchies emit as
  `oneOf` + `discriminator`; runtime polymorphism *without* those registrations,
  custom serializer settings, and validation living outside attributes
  (e.g. FluentValidation) are invisible to the spec.
- `info.title`, `info.version`, and `servers` come from the `--title`, `--version`,
  and `--server` flags (defaults: `"API"` / `"1.0.0"` / no `servers` block); there
  is no flag for `info.contact`, `info.description`, or `info.license`.

## Enum parameters are bound by ASP.NET

Rivet's enum converters are JSON converters, and only a JSON body goes through one.
An enum in the route, the query string, a header or a form field is parsed by ASP.NET,
which has never heard of `RivetCamelCaseEnumConverter`. The spec still lists the
converter's names for that parameter, because an enum has one schema wherever it
appears. So the real question is whether the host will take those names, and the two
hosts don't agree.

Minimal APIs call `Enum.TryParse`: case-sensitive, and happy to take a number. MVC
uses `EnumConverter`, which ignores case, then refuses a number that isn't a member.
For a member called `RestrictedByway2A`:

| Sent | Minimal API | MVC |
|---|---|---|
| `RestrictedByway2A`, the member name | binds | binds |
| `restrictedByway2A` (camelCase), `restrictedbyway2a` (lowercase) | 400 | binds |
| `restricted_byway2_a` (snake), `restricted-byway2-a` (kebab) | 400 | 400 |
| `0`, `+1`, ` 1` (a header loses the space before the host sees it) | binds | binds |
| `99`, which isn't a member | binds, as an undefined value | 400 |
| `1,2` | 400 | binds as `1 \| 2` when that's a member, or always on a `[Flags]` enum; otherwise 400 |

It's the same on .NET 8, 9 and 10, and the same for route, query, header and form.
`Rivet.RuntimeTests` pins all four against real Kestrel hosts, so a framework release
that changes it fails the tests.

Read across the second row and the trouble is plain: a camelCase or lowercase converter
works on an MVC action and is a 400 on a minimal API. Snake and kebab are a 400 on
both, and so is a `[JsonStringEnumMemberName]` that changes more than the casing.
Rather than publish a spec whose own values get refused, Rivet refuses the
combination:

- **`RIV4005`** (`--check`): a handler implementing a contract binds an enum from the
  route, query, header or form, and not every wire value will bind on that host.
  Minimal APIs need each value to equal its member name exactly; MVC needs it to
  equal ignoring case. Azure Functions handlers aren't checked.
- **`RIV1110`** (generation): the same rule for a `[RivetEndpoint]` or `[RivetClient]`
  action. A static `[RivetEndpoint]` is a minimal-API handler and gets the exact rule.

A contract on its own gets no check at generation, because generation can't see
how the handler binds. `--check` is where a contract-first API finds out.

The fixes, simplest first:

1. **A plain enum.** The spec says integer, and both hosts bind integers.
2. **The built-in `JsonStringEnumConverter<T>`, with no renames.** The wire names are
   the member names, which both hosts accept.
3. **Bind a `string` and parse it yourself.** The handler owns the parsing, so it can
   honour exactly the names the spec lists. A contract still advertises the enum;
   on an annotated action the parameter's type is the spec, so it becomes a
   plain string.

What the table also shows is that the hosts accept more than the spec lists:
integers, other casings on MVC, the `1,2` trick. That's ASP.NET's behaviour, and
Rivet doesn't promise it or turn it off. The promise runs one way: every value the
spec advertises binds. A client that sends anything else and gets a 200 is leaning
on the framework, not the contract.

### JSON bodies are a little loose too

In a JSON body, the Rivet converters refuse integers, but they aren't an exact
match on the spec's names. System.Text.Json also reads the C# member name in any
casing, whatever the policy, so `"RestrictedByway2A"` and `"RESTRICTEDBYWAY2A"` get
through a camelCase converter alongside `"restrictedByway2A"`.

A bare `0` is refused on every runtime. A numeric string `"0"` is refused from
.NET 9 on, but .NET 8's System.Text.Json still reads it as the member with that
value, `allowIntegerValues: false` or not. .NET 9 fixed it upstream. Rivet doesn't
drag in a newer System.Text.Json package to paper over it, so if that matters to
you, run on .NET 9 or later.

## Importer (`--from-openapi`)

A one-shot onboarding scaffold, not a sync tool. Callbacks, webhooks, links,
parameter serialization styles, multi-scheme security, and
security scheme types are out of scope (discriminator dispatch imports as a
`[JsonPolymorphic]` hierarchy when the `oneOf` mapping is usable, and falls
back loudly otherwise) — the full
honest list, including every diagnostic marker and warning category, is in the
[Import Profile](/reference/import-profile).

## Formats

- OpenAPI 3.1 is the only public output. The contract JSON consumed by `--from` is
  an internal IR shared with the sibling runtimes.
- The v1 TypeScript/Zod generators (`--compile`, `--jsonschema`) were removed in
  v2; those flags exit with an error pointing at the OpenAPI ecosystem
  replacements.
