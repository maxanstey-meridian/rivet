# Attributes

All attributes live in the `Rivet` namespace (`Rivet.Attributes` package). They are
read by Roslyn at generation time; none of them changes runtime behaviour.

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
| `[RivetOptional]` | property | Removes the property from `required` |
| `[RivetReadOnly]` / `[RivetWriteOnly]` | property | `readOnly: true` / `writeOnly: true` |
| `[RivetFormat("fmt")]` | property | `format` — for custom formats (`uri-template`, `currency`, ...) with no dedicated C# type; takes precedence over formats inferred from DataAnnotations |
| `[RivetConstraints(...)]` | property | `exclusiveMinimum`, `exclusiveMaximum`, `multipleOf`, `minItems`, `maxItems`, `uniqueItems` — constraints DataAnnotations cannot express |

## Operation metadata

| Attribute | Target | Effect |
|---|---|---|
| `[RivetRequestExample(json, ...)]` | method, contract field | Request body example (optionally named, per media type, or referencing a component example) |
| `[RivetResponseExample(status, json, ...)]` | method, contract field | Response example for a status code |
| `[ProducesFile]` | contract field | Marks the endpoint as returning a file download |

## Standard attributes Rivet also reads

- `System.ComponentModel.DataAnnotations`: `[Required]`, `[Range]`, `[MinLength]`,
  `[MaxLength]`, `[StringLength]`, `[RegularExpression]`, `[EmailAddress]`, `[Url]`
  → JSON Schema constraints and formats.
- `System.Text.Json`: `[JsonPropertyName("x")]` overrides the camelCased property
  name.
- ASP.NET: `[Route]`, `[HttpGet]`/`[HttpPost]`/..., `[ProducesResponseType]`,
  `[FromBody]`/`[FromQuery]`/`[FromForm]`/... drive operation shape on controller
  endpoints.
- `[Obsolete]` → `deprecated: true`.

None of the constraint metadata is validated by Rivet at runtime — see
[Runtime Validation](/guides/runtime-validation).
