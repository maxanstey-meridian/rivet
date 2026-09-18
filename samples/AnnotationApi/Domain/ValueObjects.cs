namespace AnnotationApi.Domain;

/// <summary>
/// Explicit scalar opt-in: [Rivet.RivetScalar] makes the contract emit this type
/// as a branded primitive (TS: string &amp; { readonly __brand: "TaskId" }) and makes
/// System.Text.Json write/read the Guid Value directly instead of an object.
/// </summary>
[Rivet.RivetScalar]
public sealed record TaskId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Explicit scalar opt-in: [Rivet.RivetScalar] makes the contract emit this type
/// as a branded primitive (TS: string &amp; { readonly __brand: "Email" }) and makes
/// System.Text.Json write/read the bare string Value.
/// </summary>
[Rivet.RivetScalar]
public sealed record Email(string Value)
{
    public override string ToString() => Value;
}
