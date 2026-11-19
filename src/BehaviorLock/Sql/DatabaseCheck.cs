using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BehaviorLock;

/// <summary>How the database of the app compares with the databases that the profile was recorded with.</summary>
public enum DatabaseMatch
{
    /// <summary>The profile names no database, so nothing can be compared.</summary>
    NotRecorded,

    /// <summary>Same provider and same version.</summary>
    Same,

    /// <summary>Same provider, other version. The SQL shapes can differ.</summary>
    OtherVersion,

    /// <summary>Other provider. The SQL shapes differ, so no SQL access matches the profile.</summary>
    OtherProvider,
}

/// <summary>The result for one database, for example <c>Microsoft.EntityFrameworkCore.Sqlite 11.0.0</c>.</summary>
public sealed record DatabaseCheckResult(DatabaseMatch Match, string Database)
{
    /// <summary>How this database reads strings and comments. The SQL normalizer needs it.</summary>
    public SqlDialect Dialect { get; } = SqlDialects.FromProvider(Database);
}

/// <summary>
/// Keeps the database provider with the profile. Each provider writes its own SQL, so a profile that was
/// recorded with one provider matches nothing on another one. Learn mode records the provider.
/// The other modes compare it with the provider that the app uses.
/// </summary>
public sealed class DatabaseCheck(
    EndpointProfile profile,
    IOptions<BehaviorLockOptions> options,
    ILogger<DatabaseCheck> logger)
{
    private readonly ConcurrentDictionary<Type, DatabaseCheckResult> _seen = new();

    /// <summary>
    /// Records or compares the database of this context. The work is done once per context type,
    /// so the call is cheap enough to make before every command.
    /// </summary>
    public DatabaseCheckResult Observe(DbContext context) =>
        _seen.TryGetValue(context.GetType(), out var known)
            ? known
            : _seen.GetOrAdd(context.GetType(), Inspect(context));

    /// <summary>
    /// Observes every context that is registered with <c>AddDbContext</c> and returns the worst result.
    /// This finds a wrong provider at startup, before the first request. A context that cannot be created
    /// here is skipped. It is still observed when its first command runs.
    /// </summary>
    public DatabaseCheckResult? ObserveRegisteredContexts(IServiceScopeFactory scopes)
    {
        DatabaseCheckResult? worst = null;

        using var scope = scopes.CreateScope();
        foreach (var contextOptions in scope.ServiceProvider.GetServices<DbContextOptions>())
        {
            try
            {
                if (scope.ServiceProvider.GetService(contextOptions.ContextType) is DbContext context)
                {
                    var result = Observe(context);
                    worst = worst is null || result.Match > worst.Match ? result : worst;
                }
            }
            catch (Exception exception)
            {
                logger.LogDebug(
                    exception,
                    "BehaviorLock cannot create {Context} at startup. Its database is checked at its first command",
                    contextOptions.ContextType);
            }
        }

        return worst;
    }

    /// <summary>Returns the provider name and version of a context, for example <c>Microsoft.EntityFrameworkCore.Sqlite 11.0.0</c>.</summary>
    public static string Describe(DbContext context)
    {
        var name = context.Database.ProviderName ?? "unknown";
        var version = context.GetInfrastructure().GetServices<IDatabaseProvider>()
            .FirstOrDefault(provider => provider.Name == name)?.Version;

        // A build can add "+commit" to its version. That part says nothing about the SQL.
        version = version?.Split('+')[0];
        return string.IsNullOrEmpty(version) ? name : $"{name} {version}";
    }

    /// <summary>Compares one database with the databases of a profile.</summary>
    public static DatabaseMatch Compare(IReadOnlyCollection<string> recorded, string database)
    {
        if (recorded.Count == 0)
        {
            return DatabaseMatch.NotRecorded;
        }

        if (recorded.Contains(database))
        {
            return DatabaseMatch.Same;
        }

        return recorded.Any(other => ProviderOf(other) == ProviderOf(database))
            ? DatabaseMatch.OtherVersion
            : DatabaseMatch.OtherProvider;
    }

    /// <summary>Says what is wrong, or returns null when the result needs no action.</summary>
    public string? Explain(DatabaseCheckResult result) => result.Match switch
    {
        DatabaseMatch.OtherProvider =>
            $"the profile was recorded with the database provider {Recorded}, and the app uses '{result.Database}'. " +
            "Each provider writes its own SQL, so no SQL access matches the profile. " +
            "Record the profile with the provider that the app uses.",
        DatabaseMatch.OtherVersion =>
            $"the profile was recorded with {Recorded}, and the app uses '{result.Database}'. " +
            "The SQL of the two versions can differ. Record the profile again.",
        _ => null,
    };

    private string Recorded => string.Join(" and ", profile.Databases.Select(database => $"'{database}'"));

    private static string ProviderOf(string database) => database.Split(' ')[0];

    private DatabaseCheckResult Inspect(DbContext context)
    {
        var database = Describe(context);

        if (options.Value.Mode == GuardMode.Learn)
        {
            profile.AddDatabase(database);
            return new DatabaseCheckResult(DatabaseMatch.Same, database);
        }

        var result = new DatabaseCheckResult(Compare(profile.Databases, database), database);
        switch (result.Match)
        {
            case DatabaseMatch.OtherProvider:
                logger.LogError("BehaviorLock: {Problem}", Explain(result));
                break;
            case DatabaseMatch.OtherVersion:
                logger.LogWarning("BehaviorLock: {Problem}", Explain(result));
                break;
        }

        return result;
    }
}
