namespace ContractApi.Domain;

/// TS: string & { readonly __brand: "Email" } — single-property record → branded primitive
public sealed record Email(string Value)
{
    public override string ToString() => Value;
}
