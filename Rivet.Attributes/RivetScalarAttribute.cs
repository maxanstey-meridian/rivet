namespace Rivet;

/// <summary>
/// Declares that the annotated class/struct/record has a scalar Rivet contract
/// representation derived from its single <c>Value</c> property: the Value type
/// determines the wire/schema inner type and the type is emitted as a branded
/// scalar instead of an object. The attribute is the explicit opt-in for the
/// scalar decision — an unannotated one-property type is an ordinary object
/// schema.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class RivetScalarAttribute : Attribute;
