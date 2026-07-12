using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace Rivet;

internal static class TypedResultValidator
{
    public static void Validate(
        string route,
        int successStatus,
        Type? successResponseType,
        IReadOnlyList<RouteErrorResponse>? errorResponses,
        IResult result,
        bool skipValidation = false
    )
    {
        if (skipValidation)
        {
            return;
        }

        var branch = Unwrap(result);

        if (branch is not IStatusCodeHttpResult statusCodeResult)
        {
            throw new RivetContractViolationException(
                $"Route '{route}' returned '{branch.GetType().FullName}', which does not expose a status code."
            );
        }

        if (statusCodeResult.StatusCode is not int actualStatusCode)
        {
            throw new RivetContractViolationException(
                $"Route '{route}' returned '{branch.GetType().FullName}' without a concrete status code."
            );
        }

        var expectedResponseType = ResolveExpectedResponseType(
            route,
            successStatus,
            successResponseType,
            errorResponses,
            actualStatusCode
        );

        ValidatePayload(route, actualStatusCode, expectedResponseType, branch);
    }

    /// <summary>
    /// Validation for file endpoints (Define.File). File results carry no
    /// IStatusCodeHttpResult — ASP.NET writes them as 200 (or 206 under range
    /// processing), so only the content type is checkable on that path.
    /// </summary>
    public static void ValidateFile(
        string route,
        int successStatus,
        string? declaredContentType,
        IReadOnlyList<RouteErrorResponse>? errorResponses,
        IResult result,
        bool skipValidation = false
    )
    {
        if (skipValidation)
        {
            return;
        }

        var branch = Unwrap(result);

        if (branch is IFileHttpResult fileResult)
        {
            ValidateFileContentType(route, declaredContentType, fileResult.ContentType);
            return;
        }

        if (branch is not IStatusCodeHttpResult { StatusCode: int actualStatusCode })
        {
            throw new RivetContractViolationException(
                $"Route '{route}' returned '{branch.GetType().FullName}', which does not expose a status code."
            );
        }

        if (actualStatusCode == successStatus)
        {
            if (branch is IValueHttpResult)
            {
                throw new RivetContractViolationException(
                    $"Route '{route}' is a file endpoint ('{declaredContentType}') but returned a JSON payload result "
                        + $"'{branch.GetType().FullName}' on the success status."
                );
            }

            if (branch is not IContentTypeHttpResult { ContentType: { } actualContentType })
            {
                throw new RivetContractViolationException(
                    $"Route '{route}' is a file endpoint ('{declaredContentType}') but returned "
                        + $"'{branch.GetType().FullName}' without file content on the success status."
                );
            }

            ValidateFileContentType(route, declaredContentType, actualContentType);
            return;
        }

        // Error branch: same rules as JSON endpoints — declared status, declared payload type.
        var expectedResponseType = ResolveExpectedResponseType(
            route,
            successStatus,
            null,
            errorResponses,
            actualStatusCode
        );
        ValidatePayload(route, actualStatusCode, expectedResponseType, branch);
    }

    private static void ValidateFileContentType(
        string route,
        string? declaredContentType,
        string? actualContentType
    )
    {
        // "image/jpeg; charset=..." satisfies a declared "image/jpeg"; a null actual
        // content type defers to ASP.NET's default for the result, which is the declared
        // type's job to match — nothing to check.
        if (
            declaredContentType is not null
            && actualContentType is not null
            && !actualContentType.StartsWith(
                declaredContentType,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new RivetContractViolationException(
                $"Route '{route}' declares content type '{declaredContentType}' but returned '{actualContentType}'."
            );
        }
    }

    private static IResult Unwrap(IResult result)
    {
        var current = result;

        while (current is INestedHttpResult nested)
        {
            current = nested.Result;
        }

        return current;
    }

    private static Type? ResolveExpectedResponseType(
        string route,
        int successStatus,
        Type? successResponseType,
        IReadOnlyList<RouteErrorResponse>? errorResponses,
        int actualStatusCode
    )
    {
        if (actualStatusCode == successStatus)
        {
            return successResponseType;
        }

        var declaredError = errorResponses?.SingleOrDefault(response =>
            response.StatusCode == actualStatusCode
        );

        if (declaredError is not null)
        {
            return declaredError.ResponseType;
        }

        var declaredStatuses = new[] { successStatus }
            .Concat(errorResponses?.Select(response => response.StatusCode) ?? [])
            .OrderBy(statusCode => statusCode)
            .ToArray();

        throw new RivetContractViolationException(
            $"Route '{route}' returned undeclared status code {actualStatusCode}. "
                + $"Declared statuses: {string.Join(", ", declaredStatuses)}."
        );
    }

    private static void ValidatePayload(
        string route,
        int actualStatusCode,
        Type? expectedResponseType,
        IResult branch
    )
    {
        var actualResponseType = ResolveActualResponseType(branch);

        if (expectedResponseType is null)
        {
            if (actualResponseType is not null)
            {
                throw new RivetContractViolationException(
                    $"Route '{route}' returned status {actualStatusCode} with payload type "
                        + $"'{actualResponseType.FullName}', but the contract declares no payload for that status."
                );
            }

            // Content-bearing results that are not IValueHttpResult (Results.Text/Content,
            // file results) still write a body — a body on a void declaration is a leak.
            if (branch is IContentTypeHttpResult)
            {
                throw new RivetContractViolationException(
                    $"Route '{route}' returned status {actualStatusCode} with a content-bearing result "
                        + $"'{branch.GetType().FullName}', but the contract declares no payload for that status."
                );
            }

            return;
        }

        if (actualResponseType is null)
        {
            throw new RivetContractViolationException(
                $"Route '{route}' returned status {actualStatusCode} without a payload, but the contract declares "
                    + $"payload type '{expectedResponseType.FullName}'."
            );
        }

        if (!expectedResponseType.IsAssignableFrom(actualResponseType))
        {
            throw new RivetContractViolationException(
                $"Route '{route}' returned status {actualStatusCode} with payload type "
                    + $"'{actualResponseType.FullName}', but the contract declares '{expectedResponseType.FullName}'."
            );
        }

        // A declared JSON payload served with a non-JSON content type contradicts the spec.
        if (
            branch is IContentTypeHttpResult { ContentType: { } contentType }
            && !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new RivetContractViolationException(
                $"Route '{route}' returned status {actualStatusCode} with content type '{contentType}', "
                    + $"but the contract declares a JSON payload ('{expectedResponseType.FullName}')."
            );
        }

        ValidateValueRuntimeType(route, actualStatusCode, expectedResponseType, branch);
    }

    /// <summary>
    /// The extra-field guard. System.Text.Json serializes the VALUE's runtime type
    /// (ASP.NET passes value.GetType()), so a derived instance — whether the handler
    /// returned Ok&lt;Derived&gt; or upcast it into Ok&lt;Declared&gt; — puts members
    /// on the wire that the spec never declared. Reject it unless the declared type is
    /// polymorphic by declaration ([JsonPolymorphic] emits oneOf into the spec) or is
    /// an interface/abstract type, where an implementing type is the only possibility.
    /// </summary>
    private static void ValidateValueRuntimeType(
        string route,
        int actualStatusCode,
        Type expectedResponseType,
        IResult branch
    )
    {
        if (branch is not IValueHttpResult { Value: { } value })
        {
            return;
        }

        var valueType = value.GetType();

        if (
            valueType == expectedResponseType
            || expectedResponseType.IsInterface
            || expectedResponseType.IsAbstract
            || expectedResponseType == typeof(object)
            // A boxed T? reports the underlying T.
            || Nullable.GetUnderlyingType(expectedResponseType) == valueType
            || expectedResponseType.GetCustomAttribute<JsonPolymorphicAttribute>() is not null
        )
        {
            return;
        }

        if (expectedResponseType.IsAssignableFrom(valueType))
        {
            throw new RivetContractViolationException(
                $"Route '{route}' returned status {actualStatusCode} with a '{valueType.FullName}' instance where "
                    + $"the contract declares '{expectedResponseType.FullName}'. The serializer writes the runtime type, "
                    + $"so undeclared members would go to the wire — map to the declared type, or mark it "
                    + $"[JsonPolymorphic] so the spec declares the hierarchy."
            );
        }
    }

    private static Type? ResolveActualResponseType(IResult branch)
    {
        var typedValueInterface = branch
            .GetType()
            .GetInterfaces()
            .FirstOrDefault(type =>
                type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IValueHttpResult<>)
            );

        if (typedValueInterface is not null)
        {
            return typedValueInterface.GetGenericArguments()[0];
        }

        if (branch is IValueHttpResult valueResult)
        {
            return valueResult.Value?.GetType() ?? typeof(object);
        }

        return null;
    }
}
