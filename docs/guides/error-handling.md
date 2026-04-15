# Error Handling

Every generated endpoint supports two modes: throw on error (default) or return a typed result.

## Default: throw on error

By default, every endpoint returns `Promise<T>` directly and throws `RivetError` on non-2xx responses:

```typescript
import { RivetError } from "~/generated/rivet";

try {
  await tasks.create({ title: "", description: null, priority: "High",
    assigneeId: null, labelNames: [] });
} catch (err) {
  if (err instanceof RivetError) {
    err.status;        // 422
    const body = err.body as { message?: string; code?: string };
    body.message;      // "Title is required" (parsed from JSON response)
    body.code;         // "VALIDATION_ERROR"
  }
}
```

### `RivetError` shape

| Property | Type | Description |
|---|---|---|
| `status` | `number` | HTTP status code |
| `body` | `unknown` | Parsed JSON response body (if any) |
| `method` | `string` | HTTP method (`GET`, `POST`, etc.) |
| `path` | `string` | Request path |
| `response` | `Response \| undefined` | Raw `fetch` Response object |

## Typed results with `unwrap: false`

Pass `{ unwrap: false }` to get a typed result instead of throwing.

### Single response type

For endpoints with a single `[ProducesResponseType]`, you get `RivetResult<T>`:

```typescript
const result = await tasks.list(1, 20, null, { unwrap: false });
result.status; // number
result.data;   // PagedResult<TaskListItemDto>
```

### Multiple response types (discriminated union)

For endpoints with multiple `[ProducesResponseType]` attributes, Rivet emits a discriminated union you can narrow by status code:

```csharp
[HttpGet("{id:guid}")]
[ProducesResponseType(typeof(TaskDetailDto), StatusCodes.Status200OK)]
[ProducesResponseType(typeof(NotFoundDto), StatusCodes.Status404NotFound)]
public async Task<IActionResult> Get(Guid id, CancellationToken ct) { ... }
```

```typescript
// Generated — discriminated union with status code narrowing
export type GetResult =
  | { status: 200; data: TaskDetailDto; response: Response }
  | { status: 404; data: NotFoundDto; response: Response }
  | { status: Exclude<number, 200 | 404>; data: unknown; response: Response };

export function get(id: string): Promise<TaskDetailDto>;
export function get(id: string, opts: { unwrap: false }): Promise<GetResult>;
```

```typescript
// Usage — narrow by status
const result = await tasks.get(id, { unwrap: false });
if (result.isOk()) {
  result.data.title;    // TaskDetailDto
} else if (result.isNotFound()) {
  result.data.message;  // NotFoundDto
}
```

## `onError` hook

Use `onError` to intercept errors before they're thrown — useful for remapping to your own error class or triggering side effects like session expiry. Fires for HTTP errors in the default (throwing) path, and also fires for network errors regardless of `unwrap`:

```typescript
configureRivet({
  baseUrl: "...",
  onError: (err) => {
    if (err.status === 401) onSessionExpired();
    throw new MyApiError(err.status, err.body);
  },
});
```

## Network errors

Network errors (DNS failure, connection refused, timeout) always throw regardless of `unwrap`. These are wrapped in `RivetError` with `status: 0` and fire `onError` if configured. The original `fetch` error is available via the `cause` property.
