namespace Rivet.Tool.Model;

/// <summary>
/// A full type declaration: export type Foo = { prop: Type; ... }
/// </summary>
public sealed record TsTypeDefinition(string Name, IReadOnlyList<TsPropertyDefinition> Properties);

/// <summary>
/// A single property within a type definition.
/// </summary>
public sealed record TsPropertyDefinition(string Name, TsType Type, bool IsOptional);
