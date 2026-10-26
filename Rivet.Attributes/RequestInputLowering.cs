namespace Rivet;

/// <summary>
/// Where a contract's <c>TInput</c> goes on the wire, decided once. Rivet.Tool lowers the
/// input there in the spec (<c>ContractWalker.BuildParams</c>), and the runtime expects ASP.NET
/// to bind it from there (<c>RouteDefinitionBase.Binding</c>); each passes the facts it reads
/// from the builder chain.
/// </summary>
internal static class RequestInputLowering
{
    /// <summary>
    /// A request has a body when a body marker declares one, or by default on POST, PUT and
    /// PATCH unless a <c>.Parameter&lt;T&gt;()</c> is declared. With no body, or a raw binary
    /// one, <c>TInput</c> is route and query parameters. Otherwise it's the body: a form when
    /// the contract declares one or the input carries files, JSON when it doesn't. A form marker
    /// declares the body itself, so <c>Define.Get&lt;T, O&gt;(..).FormEncoded()</c> is a form.
    /// </summary>
    /// <param name="method">The HTTP method, upper case, as every <c>Define</c> factory passes it.</param>
    /// <param name="bodyDeclared"><c>.RequestBody()</c> or <c>.RequestBodyRequired()</c>.</param>
    /// <param name="parametersDeclared">Any <c>.Parameter&lt;T&gt;()</c>.</param>
    /// <param name="acceptsBinary"><c>.AcceptsBinary()</c>: the body is raw bytes.</param>
    /// <param name="contentTypes">The media types declared by <c>.RequestContent[&lt;T&gt;]()</c>.</param>
    /// <param name="binaryContentDeclared">Any <c>.RequestBinaryContent()</c>.</param>
    /// <param name="formDeclared"><c>.FormEncoded()</c> or <c>.AcceptsFile()</c>.</param>
    /// <param name="formFileInput">
    /// <c>TInput</c> is an <c>IFormFile</c>, or has a property that is one or a list of them.
    /// </param>
    internal static InputBinding Of(
        string method,
        bool bodyDeclared,
        bool parametersDeclared,
        bool acceptsBinary,
        IReadOnlyCollection<string> contentTypes,
        bool binaryContentDeclared,
        bool formDeclared,
        bool formFileInput
    )
    {
        var hasBody =
            bodyDeclared
            || formDeclared
            || acceptsBinary
            || contentTypes.Count > 0
            || binaryContentDeclared
            || (method is "POST" or "PUT" or "PATCH" && !parametersDeclared);
        var binaryBody = acceptsBinary || (binaryContentDeclared && contentTypes.Count == 0);
        if (!hasBody || binaryBody)
        {
            return InputBinding.RouteAndQuery;
        }

        return formDeclared || formFileInput || contentTypes.Any(IsForm)
            ? InputBinding.Form
            : InputBinding.Json;
    }

    private static bool IsForm(string mediaType) =>
        mediaType.Split(';')[0].Trim() is var bare
        && (
            bare.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
        );
}
