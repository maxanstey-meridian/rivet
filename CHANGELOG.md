# Changelog

## Unreleased — breaking

- `Rivet.Attributes`: removed the unused `RouteDefinitionBase.IsFileUpload` getter.
- Import: an operation with `security: []` now scaffolds `.Anonymous()` instead of `.SecurityRequirements()`. Both re-emit `security: []`.
- Analysis: attributes are matched by symbol, not by simple name. A same-named attribute from another namespace (for example a custom `RequiredAttribute`) no longer counts as the Rivet, DataAnnotations or System.Text.Json attribute.
- Analysis: nested types are now discovered, so a `[RivetContract]`, `[RivetType]`, `[RivetClient]` or `[RivetEndpoint]` class nested in another class is no longer silently skipped.
- Analysis: a C# `required` field (a `[JsonInclude]` field) is now required in the schema, as a `required` property already was.
- Analysis: a contract field's builder chain is read through Roslyn operations. A chain that does not start at a `Define` member is no longer read, and a call in the chain that is not a Rivet builder method (such as a user extension named `Returns`) is refused instead of being interpreted by name.
