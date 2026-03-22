using Rivet;
using ContractApi.Domain;

namespace ContractApi.Models;

[RivetType]
public sealed record MemberDto(Guid Id, string Name, Email Email, string Role);

[RivetType]
public sealed record InviteMemberRequest(Email Email, string Role, string Nickname);

[RivetType]
public sealed record InviteMemberResponse(Guid Id);

[RivetType]
public sealed record UpdateRoleRequest(string Role);

[RivetType]
public sealed record NotFoundDto(string Message);

[RivetType]
public sealed record ValidationErrorDto(string Message, Dictionary<string, string[]> Errors);

[RivetType]
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);
