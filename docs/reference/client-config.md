# Client Configuration

The generated `rivet.ts` file exports `configureRivet()` — call it once at app startup to set the base URL, headers, and other options.

## Options

```typescript
configureRivet({
  baseUrl: "http://localhost:5000",
  headers: () => ({ Authorization: `Bearer ${token}` }),
  fetch: customFetch,
  onError: (err) => { ... },
});
```

| Option | Type | Description |
|---|---|---|
| `baseUrl` | `string` | Base URL for all API requests |
| `headers` | `() => Record<string, string> \| Promise<Record<string, string>>` | Function returning headers to include on every request (supports async) |
| `fetch` | `typeof fetch` | Custom fetch implementation |
| `onError` | `(err: RivetError) => void` | Error interceptor — see [Error Handling](/guides/error-handling#onerror-hook) for details |

## Custom fetch

The `fetch` option accepts any `typeof fetch` — use it for credentials, auth retry, or request deduplication:

```typescript
const authFetch: typeof fetch = async (input, init) => {
  const res = await fetch(input, { ...init, credentials: "include" });
  if (res.status === 401) {
    await refreshToken();
    return fetch(input, { ...init, credentials: "include" });
  }
  return res;
};

configureRivet({ baseUrl: "...", fetch: authFetch });
```

## Dynamic headers

The `headers` function is called on every request, so it always has the latest token:

```typescript
configureRivet({
  baseUrl: "...",
  headers: () => {
    const token = getStoredToken();
    return token ? { Authorization: `Bearer ${token}` } : {};
  },
});
```

## Error interceptor

`onError` fires before the error is thrown — useful for global error handling like session expiry:

```typescript
configureRivet({
  baseUrl: "...",
  onError: (err) => {
    if (err.status === 401) {
      onSessionExpired();
    }
    // Re-throw as your own error type
    throw new MyApiError(err.status, err.body);
  },
});
```

If `onError` doesn't throw, the original `RivetError` is thrown. See [Error Handling](/guides/error-handling#onerror-hook) for full semantics including `unwrap: false` and network error behaviour.
