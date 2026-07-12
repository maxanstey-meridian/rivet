using System.Text.Json;
using Microsoft.CodeAnalysis;
using Rivet.Tool.Model;

namespace Rivet.Tool.Analysis;

internal static class SecurityMetadataWalker
{
    public static ContractSecurityMetadata? Walk(Compilation compilation)
    {
        var schemes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        JsonElement? globalRequirements = null;

        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            var attributeName = attribute.AttributeClass?.ToDisplayString();
            if (
                attributeName == "Rivet.RivetSecuritySchemeAttribute"
                && attribute.ConstructorArguments is [var nameArgument, var definitionArgument]
                && nameArgument.Value is string name
                && definitionArgument.Value is string definitionJson
            )
            {
                if (!schemes.TryAdd(name, ParseJson(definitionJson, $"security scheme '{name}'")))
                {
                    throw new ContractAnalysisException(
                        $"Duplicate Rivet security scheme metadata for '{name}'."
                    );
                }
            }
            else if (
                attributeName == "Rivet.RivetGlobalSecurityAttribute"
                && attribute.ConstructorArguments is [var requirementsArgument]
                && requirementsArgument.Value is string requirementsJson
            )
            {
                if (globalRequirements is not null)
                {
                    throw new ContractAnalysisException(
                        "Multiple Rivet global security metadata declarations found."
                    );
                }

                globalRequirements = ParseJson(requirementsJson, "global security requirements");
            }
        }

        return schemes.Count == 0 && globalRequirements is null
            ? null
            : new ContractSecurityMetadata(schemes, globalRequirements);
    }

    private static JsonElement ParseJson(string json, string context)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new ContractAnalysisException(
                $"Invalid JSON in Rivet {context}: {exception.Message}"
            );
        }
    }
}
