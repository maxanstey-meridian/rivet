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
                        { IsNull: false } scopeNames,
                        { IsNull: false } scopeDescriptions,
                    ]
                && schemeArgument.Value is string schemeName
                && flowArgument.Value is string flowName
            )
            {
                var type = ParseFlowType(flowName, schemeName);
                var names = ReadStrings(scopeNames, schemeName, "scope name");
                var descriptions = ReadStrings(scopeDescriptions, schemeName, "scope description");
                if (names.Count != descriptions.Count)
                {
                    throw Diagnostics.Error(
                        Diagnostics.InvalidSecuritySchemeMetadata,
                        $"OAuth2 security scheme '{schemeName}' has mismatched scope metadata."
                    );
                }

                var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (scope, description) in names.Zip(descriptions))
                {
                    if (!scopes.TryAdd(scope, description))
                    {
                        throw Diagnostics.Error(
                            Diagnostics.InvalidSecuritySchemeMetadata,
                            $"OAuth2 security scheme '{schemeName}' declares scope '{scope}' twice "
                                + $"in its {flowName} flow."
                        );
                    }
                }

                if (!flows.TryGetValue(schemeName, out var schemeFlows))
                {
                    schemeFlows = [];
                    flows.Add(schemeName, schemeFlows);
                }
                if (schemeFlows.Any(flow => flow.Type == type))
                {
                    // OpenAPI keys a scheme's flows by type, so a second one has nowhere to go.
                    throw Diagnostics.Error(
                        Diagnostics.InvalidSecuritySchemeMetadata,
                        $"OAuth2 security scheme '{schemeName}' declares its {flowName} flow twice."
                    );
                }
                schemeFlows.Add(
                    new OAuth2Flow(
                        type,
                        authorizationUrl.Value as string,
                        tokenUrl.Value as string,
                        refreshUrl.Value as string,
                        scopes
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
                    is [
                        var schemeOrderArgument,
                        var requirementSchemeArgument,
                        { IsNull: false } scopesArgument,
                    ]
                && schemeOrderArgument.Value is int schemeOrder
                && requirementSchemeArgument.Value is string requirementScheme
            )
            {
                requirements.AddScheme(
                    schemeOrder,
                    requirementScheme,
                    ReadStrings(scopesArgument, requirementScheme, "required scope")
                );
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
                        + "has arguments Rivet can't read: a null name, flow, or scope list, or "
                        + "arguments from a Rivet.Attributes version other than the rivet tool's. Give "
                        + "it every argument its constructor names, and use the same version of both."
                );
            }
        }

        var schemes = new Dictionary<string, SecuritySchemeDefinition>(StringComparer.Ordinal);
        foreach (var (name, attribute) in schemeAttributes)
        {
            schemes.Add(name, ReadScheme(name, attribute, flows.GetValueOrDefault(name) ?? []));
        }

        // Only an oauth2 scheme reads its flows, and only a started alternative its schemes:
        // anything else would leave the spec without a word.
        foreach (var flowScheme in flows.Keys)
        {
            if (schemes.GetValueOrDefault(flowScheme) is not OAuth2SecurityScheme)
            {
                throw Diagnostics.Error(
                    Diagnostics.InvalidSecuritySchemeMetadata,
                    $"[RivetOAuthFlow] names scheme '{flowScheme}', "
                        + (
                            schemeAttributes.TryGetValue(flowScheme, out var declared)
                                ? $"whose type is '{StringArgument(declared.ConstructorArguments, 1)}'"
                                : "which no [RivetSecurityScheme] declares"
                        )
                        + ". A flow belongs to a scheme declared with type 'oauth2'."
                );
            }
        }
        if (requirements.UnstartedSchemes.ToList() is [var (unstarted, scheme), ..])
        {
            throw Diagnostics.Error(
                Diagnostics.InvalidSecuritySchemeMetadata,
                $"[RivetGlobalSecurityScheme({unstarted}, \"{scheme}\", ...)] adds to alternative "
                    + $"{unstarted}, which no [RivetGlobalSecurity({unstarted})] starts. Add one, or "
                    + "drop the scheme."
            );
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

    private static IReadOnlyList<string> ReadStrings(
        TypedConstant argument,
        string schemeName,
        string what
    ) =>
        argument
            .Values.Select(value =>
                value.Value as string
                ?? throw Diagnostics.Error(
                    Diagnostics.InvalidSecuritySchemeMetadata,
                    $"Rivet security scheme '{schemeName}' has a null {what}."
                )
            )
            .ToList();

    private static SecurityApiKeyLocation ParseLocation(string value, string schemeName) =>
        SecurityApiKeyLocations.Parse(value)
        ?? throw Diagnostics.Error(
            Diagnostics.InvalidSecuritySchemeMetadata,
            $"Rivet apiKey security scheme '{schemeName}' has unsupported location '{value}'."
        );

    private static OAuth2FlowType ParseFlowType(string value, string schemeName) =>
        OAuth2FlowTypes.Parse(value)
        ?? throw Diagnostics.Error(
            Diagnostics.InvalidSecuritySchemeMetadata,
            $"Rivet OAuth2 security scheme '{schemeName}' has unsupported flow '{value}'."
        );
}
