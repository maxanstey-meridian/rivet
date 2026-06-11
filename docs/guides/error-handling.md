# Error Handling

This page is being rewritten.

At a high level:

- declared error responses (e.g. `.Returns<NotFoundDto>(404)` on a contract, or
  `[ProducesResponseType(typeof(NotFoundDto), 404)]` on a controller) become typed
  responses in the emitted OpenAPI spec
- `openapi-fetch` surfaces them as a typed `{ data, error, response }` result — `error`
  narrows to the declared error DTO for non-2xx statuses

See [Getting Started](/getting-started) and the [Contracts guide](/guides/contracts).
