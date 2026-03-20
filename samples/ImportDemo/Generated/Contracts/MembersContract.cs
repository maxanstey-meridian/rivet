using System.Collections.Generic;
using Rivet;

namespace ImportDemo;

[RivetContract]
public static class MembersContract
{
    public static readonly RouteDefinition<List<MemberDto>> List =
        Define.Get<List<MemberDto>>("/api/members")
            .Description("List all team members");

    public static readonly RouteDefinition<MemberDto> GetById =
        Define.Get<MemberDto>("/api/members/{id}")
            .Description("Get a member by ID")
            .Returns<ErrorDto>(404, "Member not found");
}
