using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace BehaviorLock;

/// <summary>
/// Checks every EF Core command before it runs. When the reader of a command closes, it checks the number
/// of rows that the command returned.
/// <para>
/// It keeps the shapes of the command texts that passed, so a text is normalized once and not for every
/// command. See <see cref="BehaviorLockOptions.SqlShapeCacheSize"/>.
/// </para>
/// </summary>
internal sealed class GuardDbCommandInterceptor(
    IAccessGuard guard,
    ISqlNormalizer normalizer,
    DatabaseCheck? databaseCheck = null,
    IOptions<BehaviorLockOptions>? options = null) : DbCommandInterceptor
{
    private readonly SqlShapeCache _cache = new((options?.Value ?? new BehaviorLockOptions()).SqlShapeCacheSize);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Check(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Check(command, eventData);
        return new(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Check(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Check(command, eventData);
        return new(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Check(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Check(command, eventData);
        return new(result);
    }

    public override InterceptionResult DataReaderClosing(
        DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
    {
        CheckRows(command, eventData);
        return result;
    }

    public override ValueTask<InterceptionResult> DataReaderClosingAsync(
        DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
    {
        CheckRows(command, eventData);
        return new(result);
    }

    private void Check(DbCommand command, CommandEventData? eventData)
    {
        // Normalizing costs time and memory, so skip it when the guard would ignore the result.
        if (!guard.IsActive)
        {
            return;
        }

        var sql = command.CommandText;
        var dialect = DialectOf(eventData?.Context);
        var known = _cache.TryGet(sql, dialect, out var shapes);
        if (!known)
        {
            shapes = CommandShapes.Create(normalizer, sql, dialect);
        }

        foreach (var statement in shapes.Statements)
        {
            guard.Check(statement);
        }

        // Only a command that passed is kept. In Enforce mode a blocked command throws above,
        // so the texts of an attacker never fill the cache.
        if (!known)
        {
            _cache.Add(sql, dialect, shapes);
        }
    }

    /// <summary>
    /// The rows are known only now, after the app has read them. EF Core has no event for one row, and a
    /// reader of our own in place of the reader of the provider is not safe: some providers need their own type.
    /// So a blocked command has already run, and its rows are in memory. The exception stops the code that
    /// wanted to use them. EF Core closes the reader and the connection after this call, also when it throws.
    /// </summary>
    private void CheckRows(DbCommand command, DataReaderClosingEventData eventData)
    {
        if (options?.Value.CheckLimits == false || !guard.IsActive)
        {
            return;
        }

        var sql = command.CommandText;
        var dialect = DialectOf(eventData.Context);
        if (!_cache.TryGet(sql, dialect, out var shapes))
        {
            shapes = CommandShapes.Create(normalizer, sql, dialect);
        }

        guard.Check(shapes.Rows with { UpTo = eventData.ReadCount });
    }

    // Learn mode records the database provider. The other modes report a provider that the profile does not know.
    // The provider also gives the dialect. Without a context the dialect is not known, and the normalizer is careful.
    private SqlDialect DialectOf(DbContext? context) =>
        context is null ? SqlDialect.Unknown : databaseCheck?.Observe(context).Dialect ?? SqlDialect.Unknown;
}
