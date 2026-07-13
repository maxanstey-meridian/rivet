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

Bind input-bearing definitions before application execution, then select the
declared response through the bound endpoint:

```csharp
var route = Define.Post<CreateItemRequest, ItemDto>("/api/items")
    .Status(StatusCodes.Status201Created)
    .Returns<ErrorDto>(StatusCodes.Status409Conflict, "Conflict");

var endpoint = route.Bind(request);

if (await items.Exists(request.Name, ct))
{
    return endpoint
        .Error(StatusCodes.Status409Conflict, new ErrorDto("Already exists"))
        .ToActionResult();
}

var item = await items.Create(request, ct);
return endpoint.Success(item).ToActionResult();
```

Application execution is ordinary C#; `.Success(...)` and `.Error(...)` are the
contract-owned response terminals. At that boundary Rivet validates that the status
is declared and that the payload matches the declaration — an undeclared status, a
wrong or derived payload type, a body where none is declared, or an incompatible
content representation throws `RivetContractViolationException`. The first-party
`.ToActionResult()` and `.ToResult()` adapters write the result through MVC or
minimal APIs. Register `RivetContractViolationHandler` to surface violations as
`500 { "code": "contract_violation", "message": ... }` instead of an empty 500.
For the exact scope, see
[Runtime Validation](/guides/runtime-validation).

## On the consumer side

`openapi-fetch` surfaces declared errors as a typed `{ data, error, response }`
result — `error` narrows to the declared error DTO for non-2xx statuses:

```ts
const { data, error } = await api.POST("/api/members", { body });
if (error) {
  // error: ValidationErrorDto (narrowed from the declared 422)
}
```
