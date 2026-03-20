using System;
using System.Collections.Generic;
using Rivet;

namespace ImportDemo;

[RivetType]
public sealed record MemberDto(
    string Id,
    string Name,
    string Email,
    string Role);
