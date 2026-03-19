namespace Rivet;

/// <summary>
/// Result of invoking a void endpoint (no typed output).
/// </summary>
public sealed class RivetResult
{
    public int StatusCode { get; }

    public RivetResult(int statusCode)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Result of invoking a typed endpoint. Contains the status code and typed data.
/// The consumer provides a framework-specific extension to convert this to
/// IActionResult, IResult, or whatever their HTTP framework uses.
/// </summary>
public sealed class RivetResult<T>
{
    public int StatusCode { get; }
    public T Data { get; }

    public RivetResult(int statusCode, T data)
    {
        StatusCode = statusCode;
        Data = data;
    }
}
