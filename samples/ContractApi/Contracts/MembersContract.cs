using Rivet;
using ContractApi.Models;

namespace ContractApi.Contracts;

/// <summary>
/// Contract-driven endpoint definitions for the Members API.
/// Pure Rivet — no ASP.NET dependency. Controllers use .Invoke() for
/// type-safe execution with compiler-enforced input/output types.
///
/// TS: client/members.ts — list(), invite(), remove(), health()
/// </summary>
[RivetContract]
public static class MembersContract
{
    public const string ListRoute = "/api/members";
    public const string InviteRoute = "/api/members";
    public const string RemoveRoute = "/api/members/{id:guid}";
    public const string UpdateRoleRoute = "/api/members/{id:guid}/role";
    public const string HealthRoute = "/api/health";
    public const string AvatarRoute = "/api/members/{id:guid}/avatar";

    /// TS: list(): Promise<PagedResult<MemberDto>>
    public static readonly RouteDefinition<PagedResult<MemberDto>> List =
        Define.Get<PagedResult<MemberDto>>(ListRoute)
            .Description("List all team members");

    /// TS: invite(body: InviteMemberRequest): Promise<InviteMemberResponse>
    ///     with { unwrap: false } → InviteResult (201 | 422 discriminated union)
    public static readonly RouteDefinition<InviteMemberRequest, InviteMemberResponse> Invite =
        Define.Post<InviteMemberRequest, InviteMemberResponse>(InviteRoute)
            .Description("Invite a new team member")
            .Status(201)
            .Returns<ValidationErrorDto>(422, "Validation failed")
            .Secure("bearer");

    /// TS: remove(id: string): Promise<void>  — delete → remove (reserved word)
    public static readonly InputRouteDefinition<RemoveMemberInput> Remove =
        Define.Delete(RemoveRoute)
            .Accepts<RemoveMemberInput>()
            .Description("Remove a team member")
            .Returns<NotFoundDto>(404, "Member not found")
            .Secure("bearer");

    /// TS: updateRole(id: string, body: UpdateRoleRequest): Promise<void>  — input only, 204
    public static readonly InputRouteDefinition<UpdateRoleInput> UpdateRole =
        Define.Put(UpdateRoleRoute)
            .Accepts<UpdateRoleInput>()
            .Status(204)
            .Description("Update a member's role")
            .Returns<NotFoundDto>(404, "Member not found")
            .Secure("bearer");

    /// TS: health(): Promise<void>  — .Anonymous() → no auth required
    public static readonly RouteDefinition Health =
        Define.Get(HealthRoute)
            .Description("Health check")
            .Anonymous();

    /// TS: avatarUrl(id: string, token: string): string  — QueryAuth → URL builder for media players
    public static readonly FileRouteDefinition Avatar =
        Define.File(AvatarRoute)
            .ContentType("image/jpeg")
            .QueryAuth()
            .Description("Download a member's avatar");
}
