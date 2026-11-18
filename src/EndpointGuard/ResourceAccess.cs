namespace EndpointGuard;

/// <summary>One resource that a request touched, for example a SQL statement shape or an outbound host.</summary>
public sealed record ResourceAccess(string Kind, string Key)
{
    public static ResourceAccess Sql(string statementShape) => new("sql", statementShape);

    public static ResourceAccess Http(string method, string authority) => new("http", $"{method} {authority}");

    /// <summary>The form that is stored in the profile file.</summary>
    public override string ToString() => $"{Kind}: {Key}";
}

/// <summary>An access that is not in the profile of its endpoint.</summary>
public sealed record GuardViolation(string Endpoint, ResourceAccess Access, bool Blocked);

public sealed class GuardViolationException(GuardViolation violation)
    : Exception($"EndpointGuard blocked '{violation.Access}' for endpoint '{violation.Endpoint}'.")
{
    public GuardViolation Violation { get; } = violation;
}
