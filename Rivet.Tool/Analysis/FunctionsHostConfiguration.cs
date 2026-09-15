using System.Text.Json;

namespace Rivet.Tool.Analysis;

internal static class FunctionsHostConfiguration
{
    public static string LoadRoutePrefix(string projectPath)
    {
        var configured = Environment.GetEnvironmentVariable(
            "AzureFunctionsJobHost__extensions__http__routePrefix"
        );
        if (configured is not null)
        {
            return configured;
        }

        if (!projectPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return "api";
        }

        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, "host.json");
        if (!File.Exists(path))
        {
            return "api";
        }

        try
        {
            using var json = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }
            );
            var node = json.RootElement;
            foreach (var property in new[] { "extensions", "http", "routePrefix" })
            {
                if (node.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("HTTP route configuration must be an object.");
                }
                if (!node.TryGetProperty(property, out node))
                {
                    return "api";
                }
            }
            return node.GetString() ?? throw new JsonException("routePrefix must be a string.");
        }
        catch (Exception exception)
            when (exception
                    is JsonException
                        or InvalidOperationException
                        or IOException
                        or UnauthorizedAccessException
            )
        {
            throw new ContractAnalysisException(
                $"Cannot read Functions route prefix from {path}: {exception.Message}"
            );
        }
    }
}
