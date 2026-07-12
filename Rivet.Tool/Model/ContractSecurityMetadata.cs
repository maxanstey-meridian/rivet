using System.Text.Json;

namespace Rivet.Tool.Model;

public sealed record ContractSecurityMetadata(
    IReadOnlyDictionary<string, JsonElement> Schemes,
    JsonElement? GlobalRequirements = null
);
