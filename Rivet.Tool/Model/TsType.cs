namespace Rivet.Tool.Model;

/// <summary>
/// Intermediate representation of a TypeScript type expression.
/// Produced by the type walker, consumed by the emitter.
/// </summary>
public abstract record TsType
{
    private TsType() { }

    /// <summary>Leaf type: "string", "number", "boolean", "unknown".</summary>
    public sealed record Primitive(string Name) : TsType;

    /// <summary>T | null.</summary>
    public sealed record Nullable(TsType Inner) : TsType;

    /// <summary>T[].</summary>
    public sealed record Array(TsType Element) : TsType;

    /// <summary>Record&lt;string, T&gt;.</summary>
    public sealed record Dictionary(TsType Value) : TsType;

    /// <summary>"A" | "B" | "C" — string enum rendered as union.</summary>
    public sealed record StringUnion(IReadOnlyList<string> Members) : TsType;

    /// <summary>Reference to another emitted type by name.</summary>
    public sealed record TypeRef(string Name) : TsType;
}
