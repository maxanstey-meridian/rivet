namespace TaskBoard.Domain;

/// TS: string & { readonly __brand: "TaskId" } — single-property record → branded primitive
public sealed record TaskId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

/// TS: string & { readonly __brand: "Email" } — single-property record → branded primitive
public sealed record Email(string Value)
{
    public override string ToString() => Value;
}
