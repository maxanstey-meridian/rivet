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
- The spec assumes System.Text.Json's web defaults, so the adapters refuse host JSON
  options that would change the wire: another naming policy, global converters,
  ignore conditions, resolver modifiers and the rest of the
  [list](/guides/runtime-validation#runtime-guarantees-contract-responses). Declare
  that behaviour on the types instead, where generation can see it.
- A contract whose input reaches a `[JsonPolymorphic]` type needs the host to read the
  discriminator wherever it sits in the object (`AllowOutOfOrderMetadataProperties`,
  .NET 9+); without it the adapters refuse to serve the contract. On .NET 8 they always
  do, because System.Text.Json there only reads the discriminator as the first property.
  They refuse it on every runtime when the input isn't a JSON body too (a form or
  multipart contract, declared with `.FormEncoded()`, `.AcceptsFile()` or form
  `.RequestContent`, or implied by an `IFormFile` in the input; or one whose input
  comes from the route and query, such as a GET, a DELETE, a `Define.File<TInput>` or
  an `.AcceptsBinary()` endpoint), because model binding reads that input with no
  discriminator at all
  ([why](/guides/runtime-validation#runtime-guarantees-contract-responses)).
- A response header declared `required: true` has to be on the response when the
  adapter runs, which means set before the handler returns. One a middleware only
  adds in `OnStarting` arrives too late to count, and that's a trade-off, not an
  oversight. Moving the check into an `OnStarting` callback of Rivet's own doesn't
  help: the callbacks run last-registered-first, and the handler registers after the
  middleware, so Rivet's would run before the middleware's and still see no header.
  The only real fix would be a new middleware of Rivet's (call it `UseRivet()`; it
  doesn't exist) that every host registered first, so its callback ran last. That's a
  new pipeline requirement for every app, for a header style that's rare in practice,
  so it stays refused: set the header in the handler, or in middleware before the
  handler returns, and it counts.
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

- Input must compile: generation aborts on compilation errors, and on two types
  that share a simple name, whether across namespaces, nesting or generic arity
  (`RIV1007`: component names are global, so rename one).
- Enums are emitted as integers by default, matching ordinary System.Text.Json
  serialization of unannotated enums. String-valued enums require a type-level
  `[JsonConverter(typeof(JsonStringEnumConverter<T>))]`; Rivet reads only explicit
  type-level serializer metadata. A converter registered globally in the host's
  JSON options is refused when a result runs, so the spec and the wire can't part
  ways over it.
- Security scheme definitions come from `[assembly: RivetSecurityScheme(...)]` in
  source (import writes them too) or the `--security` flag; a `.Secure("name")` with no
  matching definition fails generation (`RIV2002`). Declare the document's default in
  source as well, so generation and `--check` read the same thing. Rivet describes
  security and ASP.NET enforces it. `--check` compares the two (`RIV4008`), reading
  only ASP.NET's own authorization metadata, so custom authorization filters and
  middleware are invisible to it.
- Route constraints are read from the template: the built-in ones type a path
  parameter and bound it, and one that contradicts its member stops generation
  (`RIV1125`). Custom route constraints you register in code add nothing to the spec,
  and neither do three built-in ones: `required` (every path parameter already is),
  and `file` and `nonfile`, which test for a file-name shape no schema keyword can
  say. A `regex(...)` constraint's pattern is copied in .NET syntax with its letters
  case-folded (routing matches it ignoring case, and a JSON Schema pattern has no
  flags); .NET-only constructs (class subtraction, inline options) pass through as
  written. Only ASCII letters written as themselves fold, so an escaped letter
  (`\x4a`) or a non-ASCII one matches one case in the spec and both at runtime.
- A form part's name is what the form binders that can serve the endpoint read: the C#
  name, or a rename each is given (`[DataMember(Name)]` for the minimal-API form mapper,
  `[FromForm(Name)]` for MVC). `[JsonPropertyName]` doesn't name a form part. A contract
  can be served by either host, so a rename only one binder reads is refused
  (`RIV1100`), and so is a member only one of them skips (MVC's `[BindNever]`,
  `[BindingBehavior(BindingBehavior.Never)]`, or a class-level `[Bind]` include list
  that leaves it out, or the form mapper's `[IgnoreDataMember]`) or a type only one of
  them can create. A `[RivetEndpoint]`
  method has one host: an instance method is an MVC action and a static one a
  minimal-API handler, and its parts are named as that host reads them. Only top-level
  parts are named this way; a nested complex member keeps `Inner.Value`.
- MVC's binding filters are read where MVC binds member by member: an action's form
  parts and what `--check` compares on an MVC handler. A `[Bind("A", "B")]` include list
  on the parameter and one on its class both apply (MVC runs both), a nested type's own
  list applies wherever it's bound, and `[BindNever]` or
  `[BindingBehavior(BindingBehavior.Never)]` skips a member, on the member (or the base
  property it overrides) or its class, a base class's included. A positional record's
  constructor parameter is read as MVC reads it: its own `[BindNever]` skips it, and the
  record is constructed with its default, but a class-level one doesn't reach it. Any other property filter (an `IPropertyFilterProvider`) is code Rivet can't run, so
  it stops generation and `--check` with `RIV1115` rather than guess; a minimal-API
  handler never trips it, since the form mapper reads no MVC filter.
- The spec reflects *declared* C# types and the default System.Text.Json
  conventions. `[JsonPolymorphic]`/`[JsonDerivedType]` hierarchies emit as
  `oneOf` + `discriminator`, and polymorphism that skips them doesn't get past the
  adapters: a subtype instance returned where a concrete type is declared is refused
  when the result runs, and polymorphism bolted on through `TypeInfoResolver`
  modifiers is refused with the rest of the host's
  [JSON options](/guides/runtime-validation#runtime-guarantees-contract-responses).
  A serializer attribute Rivet can't model, such as a custom `[JsonConverter]` or a
  `[JsonNumberHandling]` that writes numbers as strings, stops generation (`RIV1111`,
  `RIV1112`) rather than shipping the CLR shape. Validation living outside attributes
  (e.g. FluentValidation) is invisible to the spec.
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
| `1,2` | 400 | binds as `1 \| 2` when that's a member, or on a `[Flags]` enum when every bit is a defined flag; otherwise 400 |

It's the same on .NET 8, 9 and 10, and the same for route, query, header and form.
`Rivet.RuntimeTests` pins all four against real Kestrel hosts, so a framework release
that changes it fails the tests.

Read across the second row and the trouble is plain: a camelCase or lowercase converter
works on an MVC action and is a 400 on a minimal API. Snake and kebab are a 400 on
both, and so is a `[JsonStringEnumMemberName]` that changes more than the casing.
Rather than publish a spec whose own values get refused, Rivet refuses the
combination:

- **`RIV4005`** (`--check`): a handler implementing a contract binds an enum from the
  route, query, header, or form, and not every wire value will bind on that host.
  Minimal APIs need each value to equal its member name exactly; MVC needs it to
  equal ignoring case. An Azure Functions handler takes no name at all: the worker
  fills an enum parameter from the route or the query by parsing the raw text as JSON, and
  only an integer survives that, never a member name, quoted or not. So only a plain enum
  (or a `string`) binds there.

  A parameter the host reads as a JSON body is left alone, and so are `[FromServices]`
  and, on MVC, a parameter or enum with an explicit `[ModelBinder]`. On a minimal API an
  unattributed array comes from the query when the endpoint is mapped to a body-less
  verb such as GET, written in capitals the way `MapGet` passes it (a `MapMethods`
  `"get"` still infers a body), and any other unattributed collection is a body. An
  `[AsParameters]` type is bound member by member (with a parameterised constructor,
  only that constructor's arguments bind; a type it can't create at all, such as an
  abstract one, takes the whole endpoint down at startup, which `RIV4006` reports
  rather than this check), and so is a `[FromForm]` complex type, which goes through
  the form mapper: it binds each member by name and, unlike the rest of the minimal-API
  binder, ignores case. It names a member by its C# name or its `[DataMember(Name = ...)]`, never by a
  member's `[FromForm(Name = ...)]`, and reads every member from the form, whatever
  binding attribute it carries. On a positional record it also refuses (400) a request
  that leaves out any constructor parameter, nullable ones included; MVC binds the
  missing one as null. On MVC an unattributed collection or complex type is a body under
  `[ApiController]`, with the same two exceptions MVC makes: a complex type whose
  members carry their own binding source (a `[FromQuery]` property, an `IFormFile`) is
  bound piece by piece, and so is everything once the app sets
  `SuppressInferBindingSourcesForParameters`. Without a body, a collection is checked as
  it stands and a complex type member by member, all the way down, since MVC binds
  `?Filter.Kind=` into a nested type just as happily.
- **`RIV1110`** (generation): the same rule, with the same parameters counted, for a
  `[RivetEndpoint]` or `[RivetClient]` action. An instance method is an MVC action and
  gets the ignore-case rule; a static `[RivetEndpoint]` is a minimal-API handler and
  gets the exact one.

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

### JSON bodies take exactly the spec's names

A JSON body is stricter than the host's binders. The Rivet converters write through
System.Text.Json's own enum converter and read back exactly what it writes, nothing
more: `"restrictedByway2A"` gets through a camelCase converter, and the member name
`"RestrictedByway2A"`, `"RESTRICTEDBYWAY2A"`, `0` and `"0"` all come back as a
`JsonException` that lists the names to send. Dictionary keys follow the same rule.
It's the same on .NET 8, 9 and 10; .NET 8's System.Text.Json would happily read
`"0"` on its own, but these converters never ask it to.

## Importer (`--from-openapi`)

A one-shot onboarding scaffold, not a sync tool. Links, media-type `encoding` and
parameter serialization (`style`, `explode`, `allowReserved`, `allowEmptyValue`) come
through; what Rivet has no contract slot for is dropped and reported, never lost
quietly: callbacks (`RIV3027`), webhooks and `components.pathItems` (`RIV3028`),
parameters with `content` instead of a `schema` (`RIV3025`), and operation-level
`externalValue` examples (an `external-value` marker in the generated C#).
Discriminator dispatch imports as a `[JsonPolymorphic]` hierarchy when the `oneOf`
mapping is usable, and falls back loudly otherwise. A document OpenAPI itself calls
invalid, such as an operation whose path template has a token no path parameter
declares, stops the import (`RIV3032`) rather than being guessed into shape. The full list, including every
diagnostic marker and warning category, is in the
[Import Profile](/reference/import-profile).

## Formats

- OpenAPI 3.1 is the only public output. The contract JSON consumed by `--from` is
  an internal IR shared with the sibling runtimes.
- The v1 TypeScript/Zod generators (`--compile`, `--jsonschema`) were removed in
  v2; those flags exit with an error pointing at the OpenAPI ecosystem
  replacements.
