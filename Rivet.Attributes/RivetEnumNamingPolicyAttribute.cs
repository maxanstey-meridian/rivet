namespace Rivet;

/// <summary>
/// Names the casing convention used to derive enum wire values from the C# member
/// names. Emission-only: the contract tool reads it when lowering the enum to a
/// string union; the runtime keeps its own serializer arrangement. Meaningful only
/// on an enum that also declares a string converter — a dangling marker is refused
/// loudly (RIV1105), never silently applied.
/// </summary>
[AttributeUsage(AttributeTargets.Enum, AllowMultiple = false, Inherited = false)]
public sealed class RivetEnumNamingPolicyAttribute : Attribute
{
    public RivetEnumNamingPolicyAttribute(RivetNamingPolicy policy)
    {
        Policy = policy;
    }

    public RivetNamingPolicy Policy { get; }
}

/// <summary>
/// Member-name casing policies mirroring System.Text.Json's JsonNamingPolicy
/// surface. Applied to the exact C# member name; a member-level
/// [JsonStringEnumMemberName] always wins over the policy.
/// </summary>
public enum RivetNamingPolicy
{
    LowerCase,
    CamelCase,
    SnakeCase,
    KebabCase,
}
