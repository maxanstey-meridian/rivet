using Rivet;
using TaskBoard.Controllers;
using TaskBoard.Domain;
using Endpoint = Rivet.Endpoint;
using EndpointBuilder = Rivet.EndpointBuilder;

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
    /// TS: list(): Promise<MemberDto[]>
    public static readonly EndpointBuilder<List<MemberDto>> List =
        Endpoint.Get<List<MemberDto>>("/api/members")
            .Description("List all team members");

    /// TS: invite(body: InviteMemberRequest): Promise<InviteMemberResponse>
    ///     with { unwrap: false } → InviteResult (201 | 422 discriminated union)
    public static readonly EndpointBuilder<InviteMemberRequest, InviteMemberResponse> Invite =
        Endpoint.Post<InviteMemberRequest, InviteMemberResponse>("/api/members")
            .Description("Invite a new team member")
            .Status(201)
            .Returns<InviteMemberResponse>(422, "Validation failed")
            .Secure("admin");

    /// TS: remove(id: string): Promise<void>  — delete → remove (reserved word)
    public static readonly EndpointBuilder Remove =
        Endpoint.Delete("/api/members/{id}")
            .Description("Remove a team member")
            .Returns<MemberDto>(404, "Member not found")
            .Secure("admin");

    /// TS: health(): Promise<void>  — .Anonymous() → no auth required
    public static readonly EndpointBuilder Health =
        Endpoint.Get("/api/health")
            .Description("Health check")
            .Anonymous();
}
