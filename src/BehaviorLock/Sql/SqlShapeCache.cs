using System.Collections.Concurrent;

namespace BehaviorLock;

/// <summary>The accesses of one command text, ready for the guard.</summary>
/// <param name="Statements">One <c>sql</c> access per statement of the command.</param>
/// <param name="Rows">The <c>rows</c> limit of the command, without a real number.</param>
internal sealed record CommandShapes(ResourceAccess[] Statements, ResourceAccess Rows)
{
    public static CommandShapes Create(ISqlNormalizer normalizer, string sql, SqlDialect dialect)
    {
        var statements = normalizer.Normalize(sql, dialect);

        // One command has one reader, so the count belongs to all statements of the command together.
        // Equal statements, for example three INSERT statements, give the limit of that one shape,
        // so the number of statements is not part of the key.
        var shapes = statements.Count == 1 ? statements[0] : string.Join("; ", statements.Distinct());

        return new CommandShapes([.. statements.Select(ResourceAccess.Sql)], ResourceAccess.Rows(shapes, 0));
    }
}

/// <summary>
/// Keeps the shapes of the command texts that the guard let pass. An app sends the same few texts again
/// and again, so most commands need no normalizing. Thread-safe.
/// <para>
/// The cache must not grow without end: a text that is built from user input is new each time. So it has
/// a limit for the number of texts and for their length together. When a limit is reached, the cache
/// starts again from empty. The texts of the app come back with the next requests.
/// </para>
/// </summary>
internal sealed class SqlShapeCache(int size)
{
    // A longer text is never kept. Such a text is rare, and it would use much of the room.
    private const int MaxTextLength = 20_000;

    // The room for all texts together, in characters: about 4 MB with the default size of 1000.
    private readonly long _maxCharacters = size * 2000L;

    private readonly ConcurrentDictionary<(string Sql, SqlDialect Dialect), CommandShapes> _commands = new();
    private int _count;
    private long _characters;

    public bool TryGet(string sql, SqlDialect dialect, out CommandShapes shapes)
    {
        if (size > 0)
        {
            return _commands.TryGetValue((sql, dialect), out shapes!);
        }

        shapes = null!;
        return false;
    }

    public void Add(string sql, SqlDialect dialect, CommandShapes shapes)
    {
        if (size <= 0 || sql.Length > MaxTextLength || !_commands.TryAdd((sql, dialect), shapes))
        {
            return;
        }

        // Two threads can pass a limit at the same time. The numbers are then a little wrong until the
        // next time, and that is fine: the limits only keep the memory small.
        if (Interlocked.Increment(ref _count) > size || Interlocked.Add(ref _characters, sql.Length) > _maxCharacters)
        {
            _commands.Clear();
            Interlocked.Exchange(ref _count, 0);
            Interlocked.Exchange(ref _characters, 0);
        }
    }
}
