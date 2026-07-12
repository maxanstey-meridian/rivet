namespace Rivet.Tool.Analysis;

internal sealed class ContractAnalysisException(string message)
    : InvalidOperationException(message);
