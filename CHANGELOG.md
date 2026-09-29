# Changelog

## Unreleased — breaking

- `Rivet.Attributes`: removed the unused `RouteDefinitionBase.IsFileUpload` getter.
- Import: an operation with `security: []` now scaffolds `.Anonymous()` instead of `.SecurityRequirements()`. Both re-emit `security: []`.
- `Rivet.Attributes`: removed the protected `RouteDefinitionBase.BeginMutation()` and `CopyStateTo()` helpers. Builder state is now one immutable record behind a private gate.
- `Rivet.Attributes`: removed the 18 five-parameter `File(content, downloadName, enableRangeProcessing, lastModified, entityTag)` overloads on `RouteDefinition`, `RouteDefinition<T>`, `FileRouteDefinition` and the three `Bound*` types. Source calls still bind to the six-parameter overload (`contentType` defaults to `null`); assemblies compiled against the old overloads must be rebuilt.
- `Rivet.Attributes`: removed the test-only getters on `RouteDefinitionBase` (`EndpointSummary`, `EndpointDescription`, `IsAnonymous`, `SecurityScheme`, `FileContentType`, `IsFormEncoded`, `BinaryRequestContentType`, `RequestContentType`, `ResponseContentType`, `IsQueryAuth`, `QueryAuthParameterName`, `RouteErrorResponses`, `ResponseHeaders`, `SuccessStatusCode`, and the protected `SuccessStatus`) and the `RouteResponseHeader` record. Spec-only builder methods (`Summary`, `Description`, `Anonymous`, `Secure`, `QueryAuth`, `WithResponseHeader*`, the example, security-requirement, request-content and `Parameter<T>` markers) no longer record anything at runtime; Rivet.Tool reads them from syntax. They still throw after publication.
- `Rivet.Attributes`: declaring the same response header twice for one status no longer throws at runtime.
