# Client Configuration

Rivet no longer generates a TypeScript client of its own — the typed client comes from
[openapi-fetch](https://github.com/openapi-ts/openapi-typescript/tree/main/packages/openapi-fetch)
over the emitted spec, and configuration happens at `createClient`:

```ts
import createClient from "openapi-fetch";
import type { paths } from "./api/schema";

const api = createClient<paths>({
  baseUrl: "https://api.example.com",
  headers: { Authorization: `Bearer ${token}` },
  fetch: customFetch, // injectable fetch (e.g. app.request for local-first testing)
});
```

See the openapi-fetch documentation for middleware, custom serializers, and per-request
options.
