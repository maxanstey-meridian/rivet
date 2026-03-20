using System;
using System.Collections.Generic;
using Rivet;

namespace ImportDemo;

[RivetType]
public sealed record GetByIdInput(
    string Id);
