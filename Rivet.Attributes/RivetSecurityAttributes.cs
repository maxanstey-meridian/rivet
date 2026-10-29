namespace Rivet;

// Security for [RivetEndpoint] and [RivetClient] operations, the attribute twins of the
// contract builder's .Secure(), .AndSecure(), .OrAnonymous() and .Anonymous(). Rivet.Tool reads
// them from source; nothing reads them at runtime. The action's attributes win over its
// controller's, and a controller's over its base classes': the nearest that declares any
// security (ASP.NET's [AllowAnonymous] included) says all of it.

/// <summary>
/// One way to authenticate: the named scheme, with the scopes it must grant. Each
/// <c>[RivetSecure]</c> is an alternative (OpenAPI ORs them); <see cref="RivetAndSecureAttribute"/>
/// adds schemes the same alternative also needs.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RivetSecureAttribute(string scheme, params string[] scopes) : Attribute
{
    public string Scheme { get; } = scheme;
    public IReadOnlyList<string> Scopes { get; } = scopes;
}

/// <summary>
/// Adds a scheme, with its scopes, to the alternative the nearest <see cref="RivetSecureAttribute"/>
/// above it (in source order, on the same class or method) started: the caller must satisfy
/// both.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RivetAndSecureAttribute(string scheme, params string[] scopes) : Attribute
{
    public string Scheme { get; } = scheme;
    public IReadOnlyList<string> Scopes { get; } = scopes;
}

/// <summary>
/// Adds an alternative that needs no credentials (an empty requirement, <c>{}</c>):
/// authentication is optional.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RivetOrAnonymousAttribute : Attribute;

/// <summary>
/// No credentials required (<c>security: []</c>). Stands alone: an operation that also accepts
/// credentials uses <see cref="RivetSecureAttribute"/> plus <see cref="RivetOrAnonymousAttribute"/>.
/// ASP.NET's <c>[AllowAnonymous]</c> reads the same when no Rivet security attribute sits beside it.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RivetAnonymousAttribute : Attribute;
