using System.Security.Claims;
using System.Text;

namespace BehaviorLock;

/// <summary>
/// One thing that a request did and that the profile must allow, for example a SQL statement shape,
/// an outbound host, the kind of caller, or a number of rows.
/// </summary>
public sealed record ResourceAccess(string Kind, string Key)
{
    /// <summary>The kind of an access that says who called the endpoint.</summary>
    internal const string CallerKind = "caller";

    private static readonly ResourceAccess AnonymousCaller = new(CallerKind, "anonymous");
    private static readonly ResourceAccess CallerWithoutRole = new(CallerKind, "signed in, no role");

    /// <summary>
    /// Null for an access that the profile must list word for word.
    /// A number makes the access a limit: the profile keeps the highest number that is allowed for this
    /// <see cref="Kind"/> and <see cref="Key"/>, and every number up to it passes.
    /// In the profile file a limit has the form <c>kind: up to 100 key</c>.
    /// </summary>
    public long? UpTo { get; init; }

    /// <summary>
    /// True for a caller and for a limit. The guard checks such an access only when the profile of the
    /// endpoint has an entry to compare it with: a caller, or a limit for the same thing.
    /// A profile that an older version recorded has none, and an approved SQL statement has no limit.
    /// </summary>
    internal bool OnlyCheckedWhenRecorded => UpTo is not null || Kind == CallerKind;

    public static ResourceAccess Sql(string statementShape) => new("sql", statementShape);

    public static ResourceAccess Http(string method, string authority) => new("http", $"{method} {authority}");

    /// <summary>
    /// The rows that one command read from these statement shapes. The count is the number of read calls,
    /// so it can be one higher than the number of rows.
    /// </summary>
    public static ResourceAccess Rows(string statementShapes, long count) =>
        new("rows", $"from {statementShapes}") { UpTo = count };

    /// <summary>The size of the response body of one request.</summary>
    public static ResourceAccess ResponseSize(long bytes) => new("response", "bytes") { UpTo = bytes };

    /// <summary>
    /// The kind of caller: <c>anonymous</c>, <c>signed in, no role</c>, or the sorted list of all roles,
    /// for example <c>signed in, role admin, role support</c>. The whole list must match, so that a
    /// recording with a caller that has two roles does not allow a caller with only one of them.
    /// <para>
    /// The values of the claims in <paramref name="claimTypes"/> follow the roles, sorted by type and value,
    /// for example <c>signed in, no role, scope orders.read, scope orders.write</c>. A value with spaces
    /// counts as a list of values, like the <c>scope</c> claim of OAuth.
    /// </para>
    /// </summary>
    public static ResourceAccess Caller(ClaimsPrincipal? user, ICollection<string>? claimTypes = null)
    {
        var signedIn = false;
        var withClaims = claimTypes is { Count: > 0 };
        List<string>? roles = null;
        List<(string Type, string Value)>? others = null;

        foreach (var identity in user?.Identities ?? [])
        {
            if (!identity.IsAuthenticated)
            {
                continue;
            }

            signedIn = true;
            foreach (var claim in identity.Claims)
            {
                if (claim.Type == identity.RoleClaimType && !(roles ??= []).Contains(claim.Value))
                {
                    roles.Add(claim.Value);
                }

                if (withClaims && claimTypes!.Contains(claim.Type))
                {
                    foreach (var value in claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!(others ??= []).Contains((claim.Type, value)))
                        {
                            others.Add((claim.Type, value));
                        }
                    }
                }
            }
        }

        if (roles is null && others is null)
        {
            return signedIn ? CallerWithoutRole : AnonymousCaller;
        }

        var key = new StringBuilder("signed in");
        if (roles is null)
        {
            key.Append(", no role");
        }
        else
        {
            roles.Sort(StringComparer.Ordinal);
            foreach (var role in roles)
            {
                key.Append(", role ").Append(role);
            }
        }

        if (others is not null)
        {
            others.Sort((a, b) => string.CompareOrdinal(a.Type, b.Type) is var byType and not 0
                ? byType
                : string.CompareOrdinal(a.Value, b.Value));

            foreach (var (type, value) in others)
            {
                key.Append(", ").Append(type).Append(' ').Append(value);
            }
        }

        return new(CallerKind, key.ToString());
    }

    /// <summary>
    /// The limit as it is recorded and reported: the number is rounded up to 0, 1, 10, 100 and so on.
    /// A small change of the data then does not change the profile, and the pending file gets at most
    /// one entry per step. An access that is not a limit is returned as it is.
    /// </summary>
    internal ResourceAccess RoundedUp() => UpTo is { } number ? this with { UpTo = RoundUp(number) } : this;

    /// <summary>The form that is stored in the profile file.</summary>
    public override string ToString() => UpTo is { } number ? $"{Kind}: up to {number} {Key}" : $"{Kind}: {Key}";

    private static long RoundUp(long number)
    {
        if (number <= 0)
        {
            return 0;
        }

        long step = 1;
        while (step < number && step <= long.MaxValue / 10)
        {
            step *= 10;
        }

        // No higher step fits into the type.
        return step < number ? long.MaxValue : step;
    }
}

/// <summary>An access that is not in the profile of its endpoint.</summary>
public sealed record GuardViolation(string Endpoint, ResourceAccess Access, bool Blocked);

public sealed class GuardViolationException(GuardViolation violation)
    : Exception($"BehaviorLock blocked '{violation.Access}' for endpoint '{violation.Endpoint}'.")
{
    public GuardViolation Violation { get; } = violation;
}
