# Migrating from v0.42

Rivet reads explicit transport declarations. Make these changes before generating a new contract.

## Request parameters

Declare `[FromQuery]`, `[FromBody]`, `[FromForm]`, `[FromHeader]`, or `[FromRoute]` on controller inputs. An exact route-placeholder name and `IFormFile` remain supported conventions. Mark injected services `[FromServices]`; cancellation tokens are host plumbing. Contradictory declarations fail with RIV1100.

For example, change `Get(string search)` to `Get([FromQuery] string search)`. Use `[FromBody] Request request` for JSON.

Forms support standalone form DTOs, scalar form fields, and files with scalar fields. Combining a body DTO with separate form fields or files fails with RIV1104. Put the fields in one form DTO, split them into scalar form parameters, or use separate endpoints. Form media types must be compatible with the declared inputs; files require multipart.

## Responses

Declare response statuses and bodies for `IResult`, `IActionResult`, and other results whose status is chosen at runtime. For example, add `[ProducesResponseType(typeof(Item), 200)]` and `[ProducesResponseType(404)]` to an `IActionResult` endpoint. Fixed typed results remain supported.

A response status has one body shape. Matching attributes and typed-result branches merge; conflicting bodies fail with RIV1107. Attributes cannot hide unresolved typed-result branches.

## Scalar wrappers

A one-property record is an object unless annotated `[RivetScalar]`. The attribute also supplies System.Text.Json conversion:

```csharp
[RivetScalar]
public sealed record Email(string Value);
```

Supported wrappers are concrete, non-generic classes or structs with one public readable non-indexer `Value` property and a public constructor accepting exactly its type by value. Inherited wrapper shapes, competing type converters, and property-level System.Text.Json settings are unsupported (RIV1103). Configure converters on the inner type. Put Rivet format and schema metadata on the wrapper type; attributes on the collapsed Value property are not projected as object-property metadata. Global serializer options remain the application's responsibility and cannot be inferred by static analysis.

String-backed wrappers support dictionary keys. Other scalar wrappers do not. Nullable inner values round-trip through value-type wrappers. For reference wrappers, JSON `null` deserializes to a null wrapper; the wire cannot distinguish it from a wrapper containing null. Constructor argument validation is reported as a JSON error. Unexpected constructor exceptions retain their original meaning. Reflection-based scalar conversion does not promise trimming or Native AOT compatibility.

## Enums

Unannotated enums are numeric. Use a type-level `JsonStringEnumConverter<T>` for exact member names, or `RivetCamelCaseEnumConverter<T>`, `RivetLowerCaseEnumConverter<T>`, `RivetSnakeCaseEnumConverter<T>`, or `RivetKebabCaseEnumConverter<T>` for a naming policy. These delegate to System.Text.Json. A global string-enum converter is not visible to Rivet.

Unsupported converters, custom converter attributes, mismatched generic targets, and string flags or aliases fail with RIV1108. Use distinct values with a supported converter, or numeric serialization for flags. Duplicate wire names fail with RIV1106. Give members distinct wire names; Rivet never substitutes a numeric schema for a string converter. Numeric enum values retain their full signed/unsigned 64-bit range. Contract JSON enum integers use decimal integer syntax; fractional, exponent, string and out-of-range carriers are rejected. OpenAPI schemas which cannot form a legal C# enum (including a mixture of negative values and values above Int64.MaxValue) retain the importer's existing explicit degradation warning.

## Framework support

The runtime library supports .NET 8, 9, and 10. Basic string-enum policies work on all three. Explicit `JsonStringEnumMemberName` overrides require .NET 9 or newer, including generated enums which need such overrides. The CLI has its own .NET 9 target requirement.
