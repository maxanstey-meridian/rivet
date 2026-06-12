namespace Rivet;

/// <summary>
/// Thrown at request time when a handler's response violates its contract declaration:
/// an undeclared status code, a payload whose runtime type does not match the declared
/// type, a body where the contract declares none, or a content type the contract does
/// not promise. Subclasses <see cref="InvalidOperationException"/> so existing catch
/// blocks and tests keep working. Map it to the structured failure envelope with
/// <see cref="RivetContractViolationHandler"/>.
/// </summary>
public sealed class RivetContractViolationException(string message) : InvalidOperationException(message);
