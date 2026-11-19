using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BehaviorLock;

/// <summary>Checks every EF Core command before it runs.</summary>
public sealed class GuardDbCommandInterceptor(
    IAccessGuard guard, ISqlNormalizer normalizer, DatabaseCheck? databaseCheck = null) : DbCommandInterceptor
{
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

    private void Check(DbCommand command, CommandEventData? eventData)
    {
        // Normalizing costs time and memory, so skip it when the guard would ignore the result.
        if (!guard.IsActive)
        {
            return;
        }

        // Learn mode records the database provider. The other modes report a provider that the profile does not know.
        if (eventData?.Context is { } context)
        {
            databaseCheck?.Observe(context);
        }

        foreach (var statement in normalizer.Normalize(command.CommandText))
        {
            guard.Check(ResourceAccess.Sql(statement));
        }
    }
}
