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
(`OpenApiRoundTripTests.MaximalContract_DoublRoundTrip_IsLossless`): starting from a
maximally expressive contract, `C# → spec → import → C# → spec → import → C# → spec`
must reach a fixed point — the second and third specs are structurally identical,
and property-level fidelity (types, formats, deprecation, brands, generics) is
asserted on the final model. Every emitted `$ref` is also checked to resolve
within the document (`SpecRefResolutionTests`).

## Known non-round-tripping facts

- **Security scheme types.** The contract model carries only scheme *names*; the
  scheme *definition* in a re-emitted spec comes from the `--security` flag, not
  from the original document.
- **Anything in the importer's out-of-scope list** (callbacks, webhooks, links,
  response-header schemas beyond `string`, parameter serialization styles, ...) —
  see the [Import Profile](/reference/import-profile).

The importer remains a one-shot onboarding scaffold; round-trip stability is what
makes the scaffold trustworthy, not an invitation to ping-pong between formats.
