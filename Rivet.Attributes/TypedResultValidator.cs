using Microsoft.AspNetCore.Http;

namespace Rivet;

internal static class TypedResultValidator
{
    public static void Validate(
        string route,
        int successStatus,
        Type? successResponseType,
        IReadOnlyList<RouteErrorResponse>? errorResponses,
        IResult result)
    {
        var branch = Unwrap(result);

        if (branch is not IStatusCodeHttpResult statusCodeResult)
        {
            throw new InvalidOperationException(
                $"Route '{route}' returned '{branch.GetType().FullName}', which does not expose a status code.");
        }

        if (statusCodeResult.StatusCode is not int actualStatusCode)
        {
            throw new InvalidOperationException(
                $"Route '{route}' returned '{branch.GetType().FullName}' without a concrete status code.");
        }

        var expectedResponseType = ResolveExpectedResponseType(
            route,
            successStatus,
            successResponseType,
            errorResponses,
            actualStatusCode);

        ValidatePayload(route, actualStatusCode, expectedResponseType, branch);
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
        int actualStatusCode)
    {
        if (actualStatusCode == successStatus)
        {
            return successResponseType;
        }

        var declaredError = errorResponses?.SingleOrDefault(response => response.StatusCode == actualStatusCode);

        if (declaredError is not null)
        {
            return declaredError.ResponseType;
        }

        var declaredStatuses = new[] { successStatus }
            .Concat(errorResponses?.Select(response => response.StatusCode) ?? [])
            .OrderBy(statusCode => statusCode)
            .ToArray();

        throw new InvalidOperationException(
            $"Route '{route}' returned undeclared status code {actualStatusCode}. " +
            $"Declared statuses: {string.Join(", ", declaredStatuses)}.");
    }

    private static void ValidatePayload(
        string route,
        int actualStatusCode,
        Type? expectedResponseType,
        IResult branch)
    {
        var actualResponseType = ResolveActualResponseType(branch);

        if (expectedResponseType is null)
        {
            if (actualResponseType is not null)
            {
                throw new InvalidOperationException(
                    $"Route '{route}' returned status {actualStatusCode} with payload type " +
                    $"'{actualResponseType.FullName}', but the contract declares no payload for that status.");
            }

            return;
        }

        if (actualResponseType is null)
        {
            throw new InvalidOperationException(
                $"Route '{route}' returned status {actualStatusCode} without a payload, but the contract declares " +
                $"payload type '{expectedResponseType.FullName}'.");
        }

        if (!expectedResponseType.IsAssignableFrom(actualResponseType))
        {
            throw new InvalidOperationException(
                $"Route '{route}' returned status {actualStatusCode} with payload type " +
                $"'{actualResponseType.FullName}', but the contract declares '{expectedResponseType.FullName}'.");
        }
    }

    private static Type? ResolveActualResponseType(IResult branch)
    {
        var typedValueInterface = branch
            .GetType()
            .GetInterfaces()
            .FirstOrDefault(type =>
                type.IsGenericType &&
                type.GetGenericTypeDefinition() == typeof(IValueHttpResult<>));

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
