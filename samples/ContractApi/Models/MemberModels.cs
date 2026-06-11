using System.ComponentModel.DataAnnotations;
using Rivet;
using ContractApi.Domain;

namespace ContractApi.Models;

[RivetType]
public sealed record MemberDto(Guid Id, string Name, Email Email, string Role);

// Request DTOs validated by MVC use init properties, not a positional record:
// MVC throws at request time if a positional record parameter's property carries
// validation attributes (it requires them on the constructor parameter — where
// the Rivet spec cannot see them). Property-level attributes are visible to both
// MVC validation and the emitted spec.
[RivetType]
public sealed record InviteMemberRequest
{
    public required Email Email { get; init; }

    [Required, StringLength(20, MinimumLength = 3)]
    public required string Role { get; init; }

    [StringLength(30, MinimumLength = 2)]
    public required string Nickname { get; init; }

    [RivetConstraints(MaxItems = 5, UniqueItems = true)]
    public IReadOnlyList<string>? Tags { get; init; }
}

[RivetType]
public sealed record InviteMemberResponse(Guid Id);

[RivetType]
public sealed record UpdateRoleRequest
{
    [Required, StringLength(20, MinimumLength = 3)]
    public required string Role { get; init; }
}

[RivetType]
public sealed record NotFoundDto(string Message);

[RivetType]
public sealed record ValidationErrorDto(string Message, Dictionary<string, string[]> Errors);

[RivetType]
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);
