# OpenAPI Round-Trips

Rivet's emit and import sides are built to agree with each other: a spec emitted by
Rivet can be imported back to C#, and re-emitting from that C# produces an
equivalent spec.

The `x-rivet-*` [vendor extensions](/reference/vendor-extensions) carry the
C#-level facts that plain OpenAPI cannot express — branded value objects
(`x-rivet-brand`), monomorphised generics (`x-rivet-generic`), contract/endpoint
naming (`x-rivet-contract`/`x-rivet-endpoint`), exact C# types where the JSON
Schema is lossy (`x-rivet-csharp-type`, e.g. `DateTimeOffset` vs `DateTime`, or
`byte[]` emitted as `{ "type": "string", "contentEncoding": "base64" }`).

The test suite pins this with a double round-trip
(`OpenApiRoundTripTests.MaximalContract_DoubleRoundTrip_IsLossless`): starting from a
maximally expressive contract, `C# → spec → import → C# → spec → import → C# → spec`
must reach a fixed point — the second and third specs are structurally identical,
and property-level fidelity (types, formats, deprecation, brands, generics) is
asserted on the final model. Every emitted `$ref` is also checked to resolve
within the document (`SpecRefResolutionTests`).

## Known non-round-tripping facts

Most of what used to fall out on import now survives it: links, media-type
`encoding`, response-header schemas of any type, and parameter `style`, `explode`,
`allowReserved` and `allowEmptyValue` all come back out where they went in. What's
still lost is short, and each one says so on stderr or in the generated C#:

- **Callbacks**, on operations and in components (`RIV3027`). Contracts describe the
  requests an API receives, not the ones it sends.
- **Webhooks and `components.pathItems`** (`RIV3028`). Only path items under `paths`
  become endpoints.
- **Parameters with `content` instead of a `schema`** (`RIV3025`). A Rivet parameter
  carries a schema, so the parameter is dropped.
- **Operation-level `externalValue` examples**, which have no in-document value to
  attach to the endpoint. They leave a `[rivet:unsupported … reason=external-value]`
  marker. (A component example with `externalValue` is kept.)

The full inventory, degradations included, is in the
[Import Profile](/reference/import-profile#current-unsupported-or-degraded-surface).

The importer remains a one-shot onboarding scaffold; round-trip stability is what
makes the scaffold trustworthy, not an invitation to ping-pong between formats.
