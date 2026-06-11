# Error Handling

## Declaring error responses

On contracts, declare additional statuses with `.Returns(...)` (each status once):

```csharp
public static readonly RouteDefinition<InviteMemberRequest, InviteMemberResponse> Invite =
    Define.Post<InviteMemberRequest, InviteMemberResponse>("/api/members")
        .Status(201)
        .Returns<ValidationErrorDto>(422, "Validation failed")
        .Returns(409, "Already invited"); // status without a payload type
```

On controllers, `[ProducesResponseType(typeof(NotFoundDto), 404)]` does the same.
Either way, the emitted spec gets a typed response per declared status.

## Returning errors at runtime

Handlers that can return more than the success status use ASP.NET typed results
through `Invoke`:

```csharp
var route = Define.Post<CreateItemRequest, ItemDto>("/api/items")
    .Status(StatusCodes.Status201Created)
    .Returns<ErrorDto>(StatusCodes.Status409Conflict, "Conflict");

var result = await route.Invoke<Created<ItemDto>, Conflict<ErrorDto>>(
    request,
    req => Task.FromResult<Results<Created<ItemDto>, Conflict<ErrorDto>>>(
        TypedResults.Created($"/api/items/{req.Name}", new ItemDto("item_1", req.Name))));
```

At request time Rivet validates that the returned status code is declared and that
the payload's C# type matches the declaration — an undeclared status or wrong
payload type throws `InvalidOperationException`. That is the extent of runtime
checking: see [Runtime Validation](/guides/runtime-validation).

## On the consumer side

`openapi-fetch` surfaces declared errors as a typed `{ data, error, response }`
result — `error` narrows to the declared error DTO for non-2xx statuses:

```ts
const { data, error } = await api.POST("/api/members", { body });
if (error) {
  // error: ValidationErrorDto (narrowed from the declared 422)
}
```
