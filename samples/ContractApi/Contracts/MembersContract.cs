using Rivet;
using TaskBoard.Controllers;
using TaskBoard.Domain;

namespace TaskBoard.Contracts;

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
    /// TS: list(): Promise<PagedResult<MemberDto>>
    public static readonly RouteDefinition<PagedResult<MemberDto>> List =
        Define.Get<PagedResult<MemberDto>>("/api/members")
            .Description("List all team members");

    /// TS: invite(body: InviteMemberRequest): Promise<InviteMemberResponse>
    ///     with { unwrap: false } → InviteResult (201 | 422 discriminated union)
    public static readonly RouteDefinition<InviteMemberRequest, InviteMemberResponse> Invite =
        Define.Post<InviteMemberRequest, InviteMemberResponse>("/api/members")
            .Description("Invite a new team member")
            .Status(201)
            .Returns<ValidationErrorDto>(422, "Validation failed")
            .Secure("admin");

    /// TS: remove(id: string): Promise<void>  — delete → remove (reserved word)
    public static readonly RouteDefinition Remove =
        Define.Delete("/api/members/{id}")
            .Description("Remove a team member")
            .Returns<NotFoundDto>(404, "Member not found")
            .Secure("admin");

    /// TS: updateRole(id: string, body: UpdateRoleRequest): Promise<void>  — input only, 204
    public static readonly InputRouteDefinition<UpdateRoleRequest> UpdateRole =
        Define.Put("/api/members/{id}/role")
            .Accepts<UpdateRoleRequest>()
            .Status(204)
            .Description("Update a member's role")
            .Returns<NotFoundDto>(404, "Member not found")
            .Secure("admin");

    /// TS: health(): Promise<void>  — .Anonymous() → no auth required
    public static readonly RouteDefinition Health =
        Define.Get("/api/health")
            .Description("Health check")
            .Anonymous();
}
