using System.Text;
using System.Text.RegularExpressions;

namespace BehaviorLock;

public interface ISqlNormalizer
{
    /// <summary>
    /// Splits a command into statements and reduces each one to its shape:
    /// the SQL text with all literals and parameters replaced by <c>?</c>.
    /// </summary>
    IReadOnlyList<string> Normalize(string sql);
}

/// <summary>
/// A small scanner, not a full SQL parser. Comments stay part of the shape,
/// so an injected comment also changes the shape.
/// </summary>
public sealed partial class SqlNormalizer : ISqlNormalizer
{
    public IReadOnlyList<string> Normalize(string sql)
    {
        var statements = new List<string>();
        var current = new StringBuilder(sql.Length);
        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            var previous = i > 0 ? sql[i - 1] : '\0';

            if (c == ';')
            {
                Flush(current, statements);
                i++;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0 && current[current.Length - 1] != ' ')
                {
                    current.Append(' ');
                }

                i++;
            }
            else if (c == '\'')
            {
                // String literal.
                i = EndOfQuoted(sql, i, '\'');
                current.Append('?');
            }
            else if (c is '"' or '`')
            {
                // Quoted identifier: keep as is.
                i = CopyUntil(sql, i, EndOfQuoted(sql, i, c), current);
            }
            else if (c == '-' && next == '-')
            {
                var lineEnd = sql.IndexOf('\n', i);
                i = CopyUntil(sql, i, lineEnd < 0 ? sql.Length : lineEnd, current);
            }
            else if (c == '/' && next == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = CopyUntil(sql, i, close < 0 ? sql.Length : close + 2, current);
            }
            else if (char.IsAsciiDigit(c))
            {
                // Numeric literal, including forms like 1.5, 1e5 and 0x1F.
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.'))
                {
                    i++;
                }

                current.Append('?');
            }
            else if (c is '@' or ':' or '$' && IsNameChar(next) && previous is not ('@' or ':'))
            {
                // Parameter: @name, :name, $1. Not @@variable and not a ::cast.
                i++;
                while (i < sql.Length && IsNameChar(sql[i]))
                {
                    i++;
                }

                current.Append('?');
            }
            else if (char.IsLetter(c) || c == '_')
            {
                // Keyword or identifier. Digits inside it are kept, for example the alias "t0".
                var end = i;
                while (end < sql.Length && IsNameChar(sql[end]))
                {
                    end++;
                }

                i = CopyUntil(sql, i, end, current);
            }
            else
            {
                current.Append(c);
                i++;
            }
        }

        Flush(current, statements);
        return statements;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Returns the index after the closing quote. A doubled quote is an escaped quote.</summary>
    private static int EndOfQuoted(string sql, int start, char quote)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == quote)
            {
                if (i + 1 < sql.Length && sql[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return sql.Length;
    }

    private static int CopyUntil(string sql, int start, int end, StringBuilder target)
    {
        target.Append(sql, start, end - start);
        return end;
    }

    private static void Flush(StringBuilder current, List<string> statements)
    {
        var statement = current.ToString().Trim();
        current.Clear();

        if (statement.Length == 0)
        {
            return;
        }

        // "IN (?, ?, ?)" and multi-row "VALUES (?), (?)" must not depend on the number of values.
        statement = PlaceholderList().Replace(statement, "(?)");
        statement = PlaceholderRows().Replace(statement, "(?)");
        statements.Add(statement);
    }

    [GeneratedRegex(@"\( ?\?(?: ?, ?\?)+ ?\)")]
    private static partial Regex PlaceholderList();

    [GeneratedRegex(@"\(\?\)(?: ?, ?\(\?\))+")]
    private static partial Regex PlaceholderRows();
}
