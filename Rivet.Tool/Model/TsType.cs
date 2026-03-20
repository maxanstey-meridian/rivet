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

    /// <summary>Generic type application: Foo&lt;T, U&gt;.</summary>
    public sealed record Generic(string Name, IReadOnlyList<TsType> TypeArguments) : TsType;

    /// <summary>Unresolved generic type parameter: T, TKey, etc.</summary>
    public sealed record TypeParam(string Name) : TsType;

    /// <summary>Branded primitive: string &amp; { readonly __brand: "Email" }.</summary>
    public sealed record Brand(string Name, TsType Inner) : TsType;

    /// <summary>Inline object: { key: string; value: number }. Used for tuples.</summary>
    public sealed record InlineObject(IReadOnlyList<(string Name, TsType Type)> Fields) : TsType;

    /// <summary>
    /// Recursively collects all named type references from a TsType tree.
    /// </summary>
    public static void CollectTypeRefs(TsType type, HashSet<string> names)
    {
        switch (type)
        {
            case TypeRef r:
                names.Add(r.Name);
                break;
            case Nullable n:
                CollectTypeRefs(n.Inner, names);
                break;
            case Array a:
                CollectTypeRefs(a.Element, names);
                break;
            case Dictionary d:
                CollectTypeRefs(d.Value, names);
                break;
            case Generic g:
                names.Add(g.Name);
                foreach (var arg in g.TypeArguments)
                {
                    CollectTypeRefs(arg, names);
                }
                break;
            case Brand b:
                names.Add(b.Name);
                break;
            case InlineObject obj:
                foreach (var (_, fieldType) in obj.Fields)
                {
                    CollectTypeRefs(fieldType, names);
                }
                break;
        }
    }
}
