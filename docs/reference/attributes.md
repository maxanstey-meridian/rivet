# Attributes

All attributes live in the `Rivet` namespace (`Rivet.Attributes` package). They are
read by Roslyn at generation time; none of them changes runtime behaviour, with one
exception — `[RivetConstraints]` is a `ValidationAttribute` and is enforced by
validating hosts at runtime.

## Discovery

| Attribute | Target | Effect |
|---|---|---|
| `[RivetContract]` | static class | Marks a contract class; its `static readonly` `RouteDefinition` fields become operations. |
| `[RivetClient]` | class | Marks a controller class; all public methods with HTTP attributes (`[HttpGet]`, ...) become operations. |
| `[RivetEndpoint]` | method | Marks an individual controller/minimal-API method as an operation. |
| `[RivetType]` | class, enum | Forces a type to be walked into `components/schemas` even if no endpoint references it. |

## Schema metadata

| Attribute | Target | Effect in the spec |
|---|---|---|
| `[RivetDescription("text")]` | property, class | `description` |
| `[RivetExample("json")]` | property | `examples: [value]` (3.1 keyword; value parsed as a JSON literal) |
| `[RivetDefault("json")]` | property | `default` (JSON literal) |
| `[RivetOptional]` | property | Removes the property from `required`. Nullability alone never does; on a query, header or form parameter a nullable type is already optional. |
| `[RivetReadOnly]` / `[RivetWriteOnly]` | property | `readOnly: true` / `writeOnly: true` |
| `[RivetFormat("fmt")]` | property, class, struct, enum | `format` — for custom formats (`uri-template`, `currency`, ...) with no dedicated C# type; takes precedence over formats inferred from DataAnnotations |
| `[RivetConstraints(...)]` | property | `multipleOf`, `uniqueItems` — the two constraints DataAnnotations cannot express. Also a `ValidationAttribute`: enforced at runtime under validating hosts (`[ApiController]` model validation, `Validator.TryValidateObject`); null values pass — pair with `[Required]`. See [Runtime validation](../guides/runtime-validation.md#enforcing-constraints-at-runtime) |
| `[RivetHeader("Wire-Name")]` | property | Marks a contract input-record property as a **request header parameter** (`in: header`, original casing preserved; without a name the property name is the header name). The property never enters the JSON schema. **Spec-only** — header binding stays the host's job. `Accept`/`Content-Type`/`Authorization` are rejected by the emitter (RIV2009). |
| `[RivetScalar]` | class, struct, record | Declares an explicit **scalar value object**: the type must have one public readable non-indexer property named `Value` and a public constructor accepting its type, whose type becomes the wire primitive with `x-rivet-brand`. Opt-in replacement for the retired shape-only single-`Value`-property convention; a `Value` wrapper without it is an ordinary object schema (invalid shapes fail with RIV1103). |
| `Rivet*EnumConverter<T>` | enum | The string-enum converter family (requires .NET 8+, used inside `[JsonConverter(typeof(...))]`): `RivetLowerCaseEnumConverter<T>` / `RivetCamelCaseEnumConverter<T>` / `RivetSnakeCaseEnumConverter<T>` / `RivetKebabCaseEnumConverter<T>`. The converter class name declares both the string wire and the casing convention for enum wire values; the emitted contract values equal the runtime serializer's wire values by construction. A per-member `[JsonStringEnumMemberName]` (.NET 9+) overrides the casing. Two members casing to the same wire value fail generation (RIV1106). In a JSON body these converters read exactly the names they write, case-sensitively, on every runtime: integers, numeric strings and other casings are refused. Route, query, header and form values are bound by ASP.NET, not the converter; see [Limitations](../misc/limitations.md#enum-parameters-are-bound-by-asp-net). The bare built-in `[JsonConverter(typeof(JsonStringEnumConverter<T>))]` keeps exact C# member names, and STJ's habit of accepting integers too. |

## Operation metadata

| Attribute | Target | Effect |
|---|---|---|
| `[RivetRequestExample(json, ...)]` | method, contract field | Request body example (optionally named, per media type, or referencing a component example) |
| `[RivetResponseExample(status, json, ...)]` | method, contract field | Response example for a status code |
| `[ProducesFile]` | contract field | Marks the endpoint as returning a file download |

## Security

Two halves. The assembly attributes say which schemes exist and what the document
requires by default; the operation attributes say what one controller or action requires
instead.

### The document: schemes and its default

| Attribute | Effect |
|---|---|
| `[assembly: RivetSecurityScheme(name, type, ...)]` | Declares a scheme under `components/securitySchemes`. `type` is `"http"` (with `scheme:` such as `"bearer"`, and optionally `bearerFormat:`), `"apiKey"` (with `parameterName:` and `location:` `"header"`, `"query"` or `"cookie"`), `"oauth2"`, `"openIdConnect"` (with `openIdConnectUrl:`) or `"mutualTLS"`; `description:` goes on any of them. |
| `[assembly: RivetOAuthFlow(scheme, flow, authorizationUrl, tokenUrl, refreshUrl, scopeNames, scopeDescriptions)]` | One flow of an `oauth2` scheme: `"implicit"`, `"password"`, `"clientCredentials"` or `"authorizationCode"`, with its scopes as two parallel arrays. |
| `[assembly: RivetGlobalSecurity(order)]` | Starts alternative number `order` of the document's default `security`. An alternative nothing adds a scheme to is `{}`: credentials optional. |
| `[assembly: RivetGlobalSecurityScheme(order, scheme, scopes)]` | Adds a scheme, with the scopes it must grant, to alternative `order`. |
| `[assembly: RivetEmptyGlobalSecurity]` | The document's default is `security: []`: nothing is needed unless an operation says so. |

So a bearer API reads:

```csharp
[assembly: RivetSecurityScheme("bearer", "http", scheme: "bearer", bearerFormat: "JWT")]
[assembly: RivetGlobalSecurity(0)]
[assembly: RivetGlobalSecurityScheme(0, "bearer", new string[0])]
```

Alternatives are ORed in `order` order and a scheme's scopes accumulate, exactly the
shape `--from-openapi` writes these in. Every operation that declares nothing inherits
this default, so declare it in source rather than only on the command line:
`--security` replaces the attributes wholesale for generation and `--check` alike, and a
default that lives only in one CI script is a default the other run doesn't see.

### Operations: controllers and actions

`[RivetEndpoint]` and `[RivetClient]` operations (and the abstract methods of a
`[RivetContract]` class) say what they need with the attribute twins of the builder's
[security calls](endpoint-builder.md#security). They mean the same things and contradict
the same way (`RIV1114`).

| Attribute | Target | Builder twin | Effect |
|---|---|---|---|
| `[RivetSecure(scheme, scopes...)]` | class, method (repeatable) | `.Secure(...)` | One way in. Each one is another alternative; OpenAPI ORs them. |
| `[RivetAndSecure(scheme, scopes...)]` | class, method (repeatable) | `.AndSecure(...)` | Adds a scheme to the alternative the nearest `[RivetSecure]` above it started. |
| `[RivetOrAnonymous]` | class, method | `.OrAnonymous()` | Adds a no-credentials alternative (`{}`): authentication is optional. |
| `[RivetAnonymous]` | class, method | `.Anonymous()` | `security: []`. Stands alone. |

```csharp
[RivetSecure("bearer")]
[Authorize]                                              // what --check holds the spec to
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    [RivetEndpoint, HttpGet("")]                         // security: [{ bearer: [] }]
    public ReportList List() => ...;

    [RivetEndpoint, HttpPost("")]
    [RivetSecure("oauth", "reports:write")]              // either an OAuth token with that scope...
    [RivetSecure("apiKey"), RivetAndSecure("signature")] // ...or an API key plus a signature
    public Report Create(...) => ...;

    [RivetEndpoint, HttpGet("health"), AllowAnonymous]   // security: []
    public Health Health() => ...;
}
```

A few rules make that read the way it looks:

- **The nearest declaration wins, whole.** The action's attributes replace its
  controller's, and the controller's replace its base classes', rather than adding to
  them. That's why `Create` above doesn't also need a bearer token. An operation with no
  declaration at any level inherits the document's default.
- **Order is source order.** Rivet reads attributes from source, in the order you write
  them, so `[RivetAndSecure]` joins the `[RivetSecure]` written before it on the same
  class or method. One with no `[RivetSecure]` before it, or only a `[RivetOrAnonymous]`,
  is `RIV1114`. So is one whose class spreads its security attributes over partial
  declarations in different files, where "before" stops meaning anything.
- **`[AllowAnonymous]` counts.** ASP.NET's own attribute (anything implementing
  `IAllowAnonymous`) reads as `[RivetAnonymous]` when it's the only security attribute
  at its level. Beside Rivet's it's left to ASP.NET, which is how optional credentials
  are spelled: `[AllowAnonymous, RivetSecure("bearer"), RivetOrAnonymous]` lets anyone in
  and tells clients a token is welcome.
- **`[Authorize]` doesn't.** It names policies, and policies aren't schemes, so the spec
  can't say which credentials one wants. `--check` reads it to check the spec against what
  the handler actually lets through.

Like the builder calls, these are spec-only: nothing at runtime checks a token because of
them.

## Standard attributes Rivet also reads

- `System.ComponentModel.DataAnnotations`: `[Required]`, `[Range]`, `[MinLength]`,
  `[MaxLength]`, `[Length]`, `[StringLength]`, `[RegularExpression]`, `[EmailAddress]`, `[Url]`
  → JSON Schema constraints and formats. `[Range]` with `MinimumIsExclusive`/`MaximumIsExclusive`
  emits `exclusiveMinimum`/`exclusiveMaximum`. `[MinLength]`/`[MaxLength]`/`[Length]` emit
  `minItems`/`maxItems` on a collection and `minLength`/`maxLength` otherwise.
- `System.Text.Json`: `[JsonPropertyName("x")]` overrides the camelCased property
  name, everywhere but a form body: no form binder reads it, so a multipart or urlencoded
  part keeps the C# name. See [Part names](../guides/file-uploads.md#part-names).
- Form renames: `[FromForm(Name = "x")]` (MVC's model binder) and
  `[DataMember(Name = "x")]` (the minimal-API form mapper) name a form input's part. A
  contract's part needs both, with the same name, since either host may serve it; one
  without the other is `RIV1100`. A `[RivetEndpoint]` method's `[FromForm]` record needs only
  its one host's: the `[FromForm(Name = ...)]` on an instance method (an MVC action), the
  `[DataMember(Name = ...)]` on a static one (a minimal-API handler).
- ASP.NET: `[Route]`, `[HttpGet]`/`[HttpPost]`/..., `[ProducesResponseType]`,
  `[FromBody]`/`[FromQuery]`/`[FromForm]`/`[FromHeader]`/... drive operation shape
  on controller endpoints. `[FromHeader(Name = "X-Api-Key")]` maps to an
  `in: header` parameter with the attribute's casing (P2 wave 5; previously
  excluded with the now-retired RIV1005).
- `[Obsolete]` → `deprecated: true`.

None of the constraint metadata is validated by Rivet at runtime — see
[Runtime Validation](/guides/runtime-validation).

For scalar null semantics, dictionary keys and unsupported property metadata, see [Migrating from v0.42](/guides/migrating-from-0.42).
