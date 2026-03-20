using System.Text;

namespace Rivet.Tool.Import;

/// <summary>
/// Emits syntactically correct C# source from intermediate representations.
/// </summary>
internal static class CSharpWriter
{
    public static string WriteRecord(GeneratedRecord record, string ns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        if (record.Properties.Any(p => p.CSharpType is "IFormFile" or "IFormFile?"))
        {
            sb.AppendLine("using Microsoft.AspNetCore.Http;");
        }
        sb.AppendLine("using Rivet;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine("[RivetType]");
        var typeParamSuffix = record.TypeParameters is { Count: > 0 }
            ? $"<{string.Join(", ", record.TypeParameters)}>"
            : "";
        sb.Append($"public sealed record {record.Name}{typeParamSuffix}(");

        if (record.Properties.Count == 0)
        {
            sb.AppendLine(");");
            return sb.ToString();
        }

        sb.AppendLine();

        for (var i = 0; i < record.Properties.Count; i++)
        {
            var prop = record.Properties[i];
            var separator = i < record.Properties.Count - 1 ? "," : ");";
            sb.AppendLine($"    {prop.CSharpType} {prop.Name}{separator}");
        }

        return sb.ToString();
    }

    public static string WriteEnum(GeneratedEnum enumDef, string ns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"public enum {enumDef.Name}");
        sb.AppendLine("{");

        for (var i = 0; i < enumDef.Members.Count; i++)
        {
            var separator = i < enumDef.Members.Count - 1 ? "," : "";
            sb.AppendLine($"    {enumDef.Members[i]}{separator}");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string WriteBrand(GeneratedBrand brand, string ns)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine($"public sealed record {brand.Name}({brand.InnerType} Value)");
        sb.AppendLine("{");
        sb.AppendLine("    public override string ToString() => Value;");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string WriteContract(GeneratedContract contract, string ns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System.Collections.Generic;");
        if (contract.Fields.Any(f =>
            f.InputType is "IFormFile" or "IFormFile?"
            || f.OutputType is "IFormFile" or "IFormFile?"))
        {
            sb.AppendLine("using Microsoft.AspNetCore.Http;");
        }

        sb.AppendLine("using Rivet;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns};");
        sb.AppendLine();
        sb.AppendLine("[RivetContract]");
        sb.AppendLine($"public static class {contract.ClassName}");
        sb.AppendLine("{");

        for (var i = 0; i < contract.Fields.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine();
            }

            WriteEndpointField(sb, contract.Fields[i]);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void WriteEndpointField(StringBuilder sb, GeneratedEndpointField field)
    {
        // Unsupported markers as structured comments
        foreach (var marker in field.UnsupportedMarkers)
        {
            sb.AppendLine($"    // [rivet:unsupported {marker}]");
        }

        // Field type: RouteDefinition<TIn, TOut>, RouteDefinition<TOut>, or RouteDefinition
        var fieldType = BuildFieldType(field.InputType, field.OutputType);
        sb.Append($"    public static readonly {fieldType} {field.FieldName} =");
        sb.AppendLine();

        // Factory call
        var typeArgs = BuildTypeArgs(field.InputType, field.OutputType);
        sb.Append($"        Define.{field.HttpMethod}{typeArgs}(\"{field.Route}\")");

        // Builder chain
        var chainCalls = BuildChainCalls(field);

        if (chainCalls.Count == 0)
        {
            sb.AppendLine(";");
            return;
        }

        sb.AppendLine();

        for (var i = 0; i < chainCalls.Count; i++)
        {
            var terminator = i == chainCalls.Count - 1 ? ";" : "";
            sb.AppendLine($"            {chainCalls[i]}{terminator}");
        }
    }

    private static string BuildFieldType(string? inputType, string? outputType)
    {
        if (inputType is not null && outputType is not null)
        {
            return $"RouteDefinition<{inputType}, {outputType}>";
        }

        if (inputType is not null)
        {
            return $"InputRouteDefinition<{inputType}>";
        }

        if (outputType is not null)
        {
            return $"RouteDefinition<{outputType}>";
        }

        return "RouteDefinition";
    }

    private static string BuildTypeArgs(string? inputType, string? outputType)
    {
        if (inputType is not null && outputType is not null)
        {
            return $"<{inputType}, {outputType}>";
        }

        if (outputType is not null)
        {
            return $"<{outputType}>";
        }

        // InputRouteDefinition: type arg goes on .Accepts<T>(), not on Define.Method()
        return "";
    }

    private static List<string> BuildChainCalls(GeneratedEndpointField field)
    {
        var calls = new List<string>();

        if (field.Description is not null)
        {
            calls.Add($".Description(\"{EscapeString(field.Description)}\")");
        }

        if (field.SuccessStatus is not null)
        {
            calls.Add($".Status({field.SuccessStatus})");
        }

        // Input-only endpoint: type arg goes on .Accepts<T>()
        if (field.InputType is not null && field.OutputType is null)
        {
            calls.Add($".Accepts<{field.InputType}>()");
        }

        foreach (var error in field.ErrorResponses)
        {
            if (error.Description is not null)
            {
                calls.Add($".Returns<{error.TypeName}>({error.StatusCode}, \"{EscapeString(error.Description)}\")");
            }
            else
            {
                calls.Add($".Returns<{error.TypeName}>({error.StatusCode})");
            }
        }

        if (field.FileContentType is not null)
        {
            if (field.FileContentType == "application/octet-stream")
            {
                calls.Add(".ProducesFile()");
            }
            else
            {
                calls.Add($".ProducesFile(\"{EscapeString(field.FileContentType)}\")");
            }
        }

        if (field.IsAnonymous)
        {
            calls.Add(".Anonymous()");
        }
        else if (field.SecurityScheme is not null)
        {
            calls.Add($".Secure(\"{field.SecurityScheme}\")");
        }

        return calls;
    }

    private static string EscapeString(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }
}

// --- Contract intermediate types ---

internal sealed record GeneratedContract(
    string ClassName,
    IReadOnlyList<GeneratedEndpointField> Fields);

internal sealed record GeneratedEndpointField(
    string FieldName,
    string HttpMethod,
    string Route,
    string? InputType,
    string? OutputType,
    string? Description,
    int? SuccessStatus,
    IReadOnlyList<GeneratedErrorResponse> ErrorResponses,
    bool IsAnonymous,
    string? SecurityScheme,
    IReadOnlyList<string> UnsupportedMarkers = null!,
    string? FileContentType = null)
{
    public IReadOnlyList<string> UnsupportedMarkers { get; init; } = UnsupportedMarkers ?? [];
}

internal sealed record GeneratedErrorResponse(
    int StatusCode,
    string TypeName,
    string? Description);
