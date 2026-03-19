namespace TaskBoard.Domain;

/// <summary>Unique identifier for a task — branded in TS output.</summary>
public sealed record TaskId(Guid Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>Email address value object.</summary>
public sealed record Email(string Value)
{
    public override string ToString() => Value;
}
