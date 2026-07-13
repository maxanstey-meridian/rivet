using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Rivet.Tool;

internal static class ImportedSourceFingerprint
{
    private const string Prefix = "sha256-v1:";

    public static string Compute(string source) =>
        Compute(CSharpSyntaxTree.ParseText(source).GetRoot());

    public static string Compute(SyntaxNode root)
    {
        var canonical = root.WithoutTrivia().NormalizeWhitespace(eol: "\n").ToFullString();
        return Prefix
            + Convert
                .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
    }
}
