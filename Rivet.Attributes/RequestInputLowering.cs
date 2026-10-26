namespace Rivet;

using System.Reflection;
using Microsoft.AspNetCore.Http;

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
    /// <c>TInput</c> is an <c>IFormFile</c>, or one of the properties ASP.NET's form binder
    /// would bind is one or a list of them: see <see cref="CarriesFormFiles"/>.
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

    /// <summary>
    /// The runtime's <c>formFileInput</c>: <c>TInput</c> is an <c>IFormFile</c>, or one of the
    /// properties ASP.NET's form binder would bind is an <c>IFormFile</c>, an array of them or
    /// one of the tool's collections of them. Those are the public instance properties that
    /// aren't indexers, along the whole base chain, where the most-derived property of a name,
    /// whatever its accessibility, hides the base's, so a <c>new</c> member hides its base's. Roslyn symbols and <c>System.Type</c> can't share
    /// code, so the tool keeps its own copy (<c>ContractWalker.HasFormFileProperty</c>), and
    /// <c>FormFileTests.Tool_and_runtime_agree_on_which_inputs_carry_files</c> holds the two
    /// to one table of shapes.
    /// </summary>
    internal static bool CarriesFormFiles(Type inputType)
    {
        if (inputType == typeof(IFormFile))
        {
            return true;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var type = inputType; type is not null; type = type.BaseType)
        {
            foreach (
                var property in type.GetProperties(
                    BindingFlags.Public
                        | BindingFlags.NonPublic
                        | BindingFlags.Instance
                        | BindingFlags.DeclaredOnly
                )
            )
            {
                // Any instance property hides a base's of the same name; only a public one
                // (a public accessor, as BindingFlags.Public reads it) can carry files.
                if (
                    property.GetIndexParameters().Length == 0
                    && seen.Add(property.Name)
                    && (
                        property.GetMethod?.IsPublic == true || property.SetMethod?.IsPublic == true
                    )
                    && IsFormFiles(property.PropertyType)
                )
                {
                    return true;
                }
            }
        }

        return false;
    }

    // An IFormFile, or an array, List<T> or sequence interface of them: the tool's
    // TypeWalker.IsCollectionOf, held to it by FormFileTests.Tool_and_runtime_agree_on_which_inputs_carry_files.
    private static bool IsFormFiles(Type type) =>
        type == typeof(IFormFile)
        || (type.IsArray && type.GetElementType() == typeof(IFormFile))
        || (
            type is { IsGenericType: true, GenericTypeArguments: [var element] }
            && element == typeof(IFormFile)
            && _formFileCollections.Contains(type.GetGenericTypeDefinition())
        );

    private static readonly Type[] _formFileCollections =
    [
        typeof(List<>),
        typeof(IList<>),
        typeof(ICollection<>),
        typeof(IEnumerable<>),
        typeof(IReadOnlyList<>),
        typeof(IReadOnlyCollection<>),
    ];

    private static bool IsForm(string mediaType) =>
        mediaType.Split(';')[0].Trim() is var bare
        && (
            bare.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            || bare.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
        );
}
