# Changelog

## Unreleased — breaking

- `Rivet.Attributes`: removed the unused `RouteDefinitionBase.IsFileUpload` getter.
- Import: an operation with `security: []` now scaffolds `.Anonymous()` instead of `.SecurityRequirements()`. Both re-emit `security: []`.
- Import: every disambiguated name now uses one `Name_2`, `Name_3`… scheme. Synthetic types that used to be `Foo2` are now `Foo_2` (for example, slack's `InfoResponseDefaultContent12` is now `InfoResponseDefaultContent1_2`). Enum members and contract fields also skip a suffix that the spec already authored.
- Import: inline schemas that differ in `oneOf`/`anyOf`/`allOf`/`const`/`not` now get distinct synthetic types instead of silently sharing the first one.
- Import: a Swagger 2 `basic` security definition now imports as HTTP `basic`; before, it was refused.
- Import: a malformed spec (invalid JSON, missing required fields) now exits 1 with `error: invalid OpenAPI document: …` instead of a stack trace.
