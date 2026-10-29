using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

internal static class SecurityMetadataWalker
{
    public static ContractSecurityMetadata? Walk(Compilation compilation)
    {
        var schemeAttributes = new Dictionary<string, AttributeData>(StringComparer.Ordinal);
        var flows = new Dictionary<string, List<OAuth2Flow>>(StringComparer.Ordinal);
        var requirements = new SecurityRequirementsBuilder();
        var hasEmptyGlobalSecurity = false;
        var schemeType = compilation.GetTypeByMetadataName("Rivet.RivetSecuritySchemeAttribute");
        var flowType = compilation.GetTypeByMetadataName("Rivet.RivetOAuthFlowAttribute");
        var globalType = compilation.GetTypeByMetadataName("Rivet.RivetGlobalSecurityAttribute");
        var globalSchemeType = compilation.GetTypeByMetadataName(
            "Rivet.RivetGlobalSecuritySchemeAttribute"
        );
        var emptyGlobalType = compilation.GetTypeByMetadataName(
            "Rivet.RivetEmptyGlobalSecurityAttribute"
        );

        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            if (
                attribute.Is(schemeType)
                && attribute.ConstructorArguments is [var nameArgument, ..]
                && nameArgument.Value is string name
            )
            {
                if (!schemeAttributes.TryAdd(name, attribute))
                {
                    throw Diagnostics.Error(
                        Diagnostics.InvalidSecuritySchemeMetadata,
                        $"duplicate Rivet security scheme metadata for '{name}'."
                    );
                }
            }
            else if (
                attribute.Is(flowType)
                && attribute.ConstructorArguments
                    is [
                        var schemeArgument,
                        var flowArgument,
                        var authorizationUrl,
                        var tokenUrl,
                        var refreshUrl,
                        var scopeNames,
                        var scopeDescriptions,
                    ]
                && schemeArgument.Value is string schemeName
                && flowArgument.Value is string flowName
            )
            {
                var names = ReadStrings(scopeNames);
                var descriptions = ReadStrings(scopeDescriptions);
                if (names.Count != descriptions.Count)
                {
                    throw Diagnostics.Error(
                        Diagnostics.InvalidSecuritySchemeMetadata,
                        $"OAuth2 security scheme '{schemeName}' has mismatched scope metadata."
                    );
                }

                if (!flows.TryGetValue(schemeName, out var schemeFlows))
                {
                    schemeFlows = [];
                    flows.Add(schemeName, schemeFlows);
                }
                schemeFlows.Add(
                    new OAuth2Flow(
                        ParseFlowType(flowName, schemeName),
                        authorizationUrl.Value as string,
                        tokenUrl.Value as string,
                        refreshUrl.Value as string,
                        names
                            .Zip(descriptions)
                            .ToDictionary(pair => pair.First, pair => pair.Second)
                    )
                );
            }
            else if (
                attribute.Is(globalType)
                && attribute.ConstructorArguments is [var orderArgument]
                && orderArgument.Value is int order
            )
            {
                requirements.AddRequirement(order);
            }
            else if (
                attribute.Is(globalSchemeType)
                && attribute.ConstructorArguments
                    is [var schemeOrderArgument, var requirementSchemeArgument, var scopesArgument]
                && schemeOrderArgument.Value is int schemeOrder
                && requirementSchemeArgument.Value is string requirementScheme
            )
            {
                requirements.AddScheme(schemeOrder, requirementScheme, ReadStrings(scopesArgument));
            }
            else if (attribute.Is(emptyGlobalType))
            {
                hasEmptyGlobalSecurity = true;
            }
            else if (
                attribute.Is(schemeType)
                || attribute.Is(flowType)
                || attribute.Is(globalType)
                || attribute.Is(globalSchemeType)
            )
            {
                // One of ours whose arguments don't have the shape read above: skipping it
                // would drop the scheme, flow, or requirement from the spec without a word.
                throw Diagnostics.Error(
                    Diagnostics.InvalidSecuritySchemeMetadata,
                    $"[{attribute.AttributeClass!.Name.Replace("Attribute", "", StringComparison.Ordinal)}] "
                        + $"at {attribute.ApplicationSyntaxReference?.GetSyntax().SourcePosition() ?? "assembly level"} "
                        + "has arguments Rivet can't read: a null scheme name or flow, or arguments "
                        + "from a Rivet.Attributes version other than the rivet tool's. Give it every "
                        + "argument its constructor names, and use the same version of both."
                );
            }
        }

        var schemes = new Dictionary<string, SecuritySchemeDefinition>(StringComparer.Ordinal);
        foreach (var (name, attribute) in schemeAttributes)
        {
            schemes.Add(name, ReadScheme(name, attribute, flows.GetValueOrDefault(name) ?? []));
        }

        var globalRequirements =
            requirements.Build() ?? (hasEmptyGlobalSecurity ? new SecurityRequirements([]) : null);

        return schemes.Count == 0 && globalRequirements is null
            ? null
            : new ContractSecurityMetadata(schemes, globalRequirements);
    }

    private static SecuritySchemeDefinition ReadScheme(
        string name,
        AttributeData attribute,
        IReadOnlyList<OAuth2Flow> flows
    )
    {
        var arguments = attribute.ConstructorArguments;
        var type = RequiredStringArgument(arguments, 1, name, "type");
        var description = StringArgument(arguments, 2);
        return type switch
        {
            "apiKey" => new ApiKeySecurityScheme(
                RequiredStringArgument(arguments, 3, name, "parameter name"),
                ParseLocation(RequiredStringArgument(arguments, 4, name, "location"), name),
                description
            ),
            "http" => new HttpSecurityScheme(
                RequiredStringArgument(arguments, 5, name, "HTTP scheme"),
                StringArgument(arguments, 6),
                description
            ),
            "oauth2" => new OAuth2SecurityScheme(flows, description),
            "openIdConnect" => new OpenIdConnectSecurityScheme(
                RequiredStringArgument(arguments, 7, name, "OpenID Connect URL"),
                description
            ),
            "mutualTLS" => new MutualTlsSecurityScheme(description),
            _ => throw Diagnostics.Error(
                Diagnostics.InvalidSecuritySchemeMetadata,
                $"Rivet security scheme '{name}' has unsupported type '{type}'."
            ),
        };
    }

    private static string? StringArgument(ImmutableArray<TypedConstant> arguments, int index) =>
        index < arguments.Length ? arguments[index].Value as string : null;

    private static string RequiredStringArgument(
        ImmutableArray<TypedConstant> arguments,
        int index,
        string schemeName,
        string field
    ) =>
        StringArgument(arguments, index)
        ?? throw Diagnostics.Error(
            Diagnostics.InvalidSecuritySchemeMetadata,
            $"Rivet security scheme '{schemeName}' is missing its {field}."
        );

    private static IReadOnlyList<string> ReadStrings(TypedConstant argument) =>
        argument.Values.Select(value => value.Value as string ?? "").ToList();

    private static SecurityApiKeyLocation ParseLocation(string value, string schemeName) =>
        SecurityApiKeyLocations.Parse(value)
        ?? throw Diagnostics.Error(
            Diagnostics.InvalidSecuritySchemeMetadata,
            $"Rivet apiKey security scheme '{schemeName}' has unsupported location '{value}'."
        );

    private static OAuth2FlowType ParseFlowType(string value, string schemeName) =>
        value switch
        {
            "implicit" => OAuth2FlowType.Implicit,
            "password" => OAuth2FlowType.Password,
            "clientCredentials" => OAuth2FlowType.ClientCredentials,
            "authorizationCode" => OAuth2FlowType.AuthorizationCode,
            _ => throw Diagnostics.Error(
                Diagnostics.InvalidSecuritySchemeMetadata,
                $"Rivet OAuth2 security scheme '{schemeName}' has unsupported flow '{value}'."
            ),
        };
}
