using System.Text;
using System.Text.RegularExpressions;

namespace BehaviorLock;

public interface ISqlNormalizer
{
    /// <summary>
    /// Splits a command into statements and reduces each one to its shape:
    /// the SQL text with all literals and parameters replaced by <c>?</c>.
    /// The dialect says how the database reads strings and comments.
    /// When the normalizer cannot be sure where a string or a comment ends,
    /// it returns the whole command as it is, as one statement.
    /// </summary>
    IReadOnlyList<string> Normalize(string sql, SqlDialect dialect = SqlDialect.Unknown);
}

/// <summary>
/// A small scanner, not a full SQL parser. Comments and quoted identifiers stay part of the shape,
/// so an injected comment also changes the shape.
/// <para>
/// The scanner must find the end of each string exactly where the database finds it. If it does not,
/// it replaces text by <c>?</c> that the database runs as code. Databases differ here, so the rules
/// come from the <see cref="SqlDialect"/>. Where the rules of the database are not sure, the scanner
/// reads the text both ways. When the two ways differ, it does not normalize the command.
/// </para>
/// </summary>
public sealed partial class SqlNormalizer : ISqlNormalizer
{
    // Marks a parameter while the statement is built. In the result it is '?', like a literal.
    // Only lists of parameters are reduced to one placeholder, and the mark tells them from literals.
    private const char ParameterMark = '\u0001';
    private const string ParameterGroup = "(\u0001)";

    public IReadOnlyList<string> Normalize(string sql, SqlDialect dialect = SqlDialect.Unknown)
    {
        var rules = DialectRules.For(dialect);

        // The mark has no meaning in SQL. In the input it must not look like a parameter.
        if (sql.Contains(ParameterMark))
        {
            sql = sql.Replace(ParameterMark, '�');
        }

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
                if (!TryFindEndOfQuoted(sql, i, '\'', rules.BackslashEscapes, out var end))
                {
                    return AsItIs(sql);
                }

                current.Append('?');
                i = end;
            }
            else if (c == '"')
            {
                // Quoted identifier: keep as is. MySQL can read it as a string, so backslashes matter here too.
                if (!TryFindEndOfQuoted(sql, i, '"', rules.BackslashEscapes, out var end))
                {
                    return AsItIs(sql);
                }

                i = CopyUntil(sql, i, end, current);
            }
            else if (c == '`')
            {
                // Quoted identifier of MySQL and SQLite: keep as is.
                i = CopyUntil(sql, i, EndOfQuoted(sql, i, '`', backslashEscapes: false), current);
            }
            else if (c == '-' && next == '-')
            {
                i = CopyUntil(sql, i, EndOfLine(sql, i), current);
            }
            else if (c == '#' && rules.HashComments != Reads.No)
            {
                // A comment in MySQL, an operator or a part of a name in other databases.
                if (rules.HashComments == Reads.Unsure)
                {
                    return AsItIs(sql);
                }

                i = CopyUntil(sql, i, EndOfLine(sql, i), current);
            }
            else if (c == '/' && next == '*')
            {
                var plain = EndOfComment(sql, i, nested: false);
                var nested = EndOfComment(sql, i, nested: true);
                if (rules.NestedComments == Reads.Unsure && plain != nested)
                {
                    return AsItIs(sql);
                }

                i = CopyUntil(sql, i, rules.NestedComments == Reads.Yes ? nested : plain, current);
            }
            else if (c == '$' && rules.DollarQuotes != Reads.No && DollarQuoteLength(sql, i) is var length and > 0)
            {
                // $$...$$ or $tag$...$tag$ is a string literal in PostgreSQL.
                if (rules.DollarQuotes == Reads.Unsure)
                {
                    return AsItIs(sql);
                }

                var close = sql.IndexOf(sql.Substring(i, length), i + length, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + length;
                current.Append('?');
            }
            else if (char.IsAsciiDigit(c))
            {
                // Numeric literal. Letters directly after it are not part of it, so they stay in the shape.
                i = EndOfNumber(sql, i);
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

                current.Append(ParameterMark);
            }
            else if (char.IsLetter(c) || c == '_')
            {
                // Keyword or identifier. Digits inside it are kept, for example the alias "t0".
                // PostgreSQL also allows '$' inside a name, and there it does not start a dollar quote.
                var end = i;
                while (end < sql.Length && (IsNameChar(sql[end]) || (sql[end] == '$' && rules.DollarQuotes == Reads.Yes)))
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

    /// <summary>
    /// The command without any change. It can only match a profile entry with the same text,
    /// and the same text does the same thing, however the database reads it.
    /// </summary>
    private static string[] AsItIs(string sql) => [sql.Trim()];

    /// <summary>
    /// Finds the index after the closing quote. Returns false when the database could read the text in
    /// two ways that end at different places, for example <c>'a\'</c>.
    /// </summary>
    private static bool TryFindEndOfQuoted(string sql, int start, char quote, Reads backslashEscapes, out int end)
    {
        end = EndOfQuoted(sql, start, quote, backslashEscapes: false);

        // Without a backslash both ways of reading are the same.
        if (backslashEscapes == Reads.No || sql.IndexOf('\\', start, end - start) < 0)
        {
            return true;
        }

        var escaped = EndOfQuoted(sql, start, quote, backslashEscapes: true);
        if (backslashEscapes == Reads.Unsure && escaped != end)
        {
            return false;
        }

        end = escaped;
        return true;
    }

    /// <summary>
    /// Returns the index after the closing quote. A doubled quote is an escaped quote.
    /// With backslash escapes, a backslash also escapes the character after it.
    /// </summary>
    private static int EndOfQuoted(string sql, int start, char quote, bool backslashEscapes)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (backslashEscapes && sql[i] == '\\')
            {
                i += 2;
                continue;
            }

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

    private static int EndOfLine(string sql, int start)
    {
        var lineEnd = sql.IndexOf('\n', start);
        return lineEnd < 0 ? sql.Length : lineEnd;
    }

    /// <summary>Returns the index after the comment. A nested comment ends when every inner comment has ended.</summary>
    private static int EndOfComment(string sql, int start, bool nested)
    {
        var depth = 1;
        var i = start + 2;
        while (i + 1 < sql.Length)
        {
            if (sql[i] == '*' && sql[i + 1] == '/')
            {
                i += 2;
                if (--depth == 0)
                {
                    return i;
                }
            }
            else if (nested && sql[i] == '/' && sql[i + 1] == '*')
            {
                depth++;
                i += 2;
            }
            else
            {
                i++;
            }
        }

        return sql.Length;
    }

    /// <summary>Returns the length of <c>$$</c> or <c>$tag$</c> at this index, or 0 when there is none.</summary>
    private static int DollarQuoteLength(string sql, int start)
    {
        var i = start + 1;
        if (i < sql.Length && (char.IsLetter(sql[i]) || sql[i] == '_'))
        {
            while (i < sql.Length && IsNameChar(sql[i]))
            {
                i++;
            }
        }

        return i < sql.Length && sql[i] == '$' ? i - start + 1 : 0;
    }

    /// <summary>Returns the index after a number such as 12, 1.5, 1e5, 1.5E-3 or 0x1F.</summary>
    private static int EndOfNumber(string sql, int start)
    {
        var i = start;

        if (sql[i] == '0' && i + 2 < sql.Length && sql[i + 1] is 'x' or 'X' && char.IsAsciiHexDigit(sql[i + 2]))
        {
            i += 2;
            while (i < sql.Length && char.IsAsciiHexDigit(sql[i]))
            {
                i++;
            }

            return i;
        }

        i = SkipDigits(sql, i);
        if (i < sql.Length && sql[i] == '.')
        {
            i = SkipDigits(sql, i + 1);
        }

        if (i < sql.Length && sql[i] is 'e' or 'E')
        {
            var exponent = i + 1;
            if (exponent < sql.Length && sql[exponent] is '+' or '-')
            {
                exponent++;
            }

            if (exponent < sql.Length && char.IsAsciiDigit(sql[exponent]))
            {
                i = SkipDigits(sql, exponent);
            }
        }

        return i;
    }

    private static int SkipDigits(string sql, int i)
    {
        while (i < sql.Length && char.IsAsciiDigit(sql[i]))
        {
            i++;
        }

        return i;
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

        // "IN (@a, @b, @c)" and multi-row "VALUES (@a), (@b)" must not depend on the number of parameters.
        // Lists with a literal are kept: a literal can come from an attacker, who could add a value or a row.
        statement = ParameterList().Replace(statement, ParameterGroup);
        statement = ParameterRows().Replace(statement, ParameterGroup);
        statements.Add(statement.Replace(ParameterMark, '?'));
    }

    [GeneratedRegex(@"\( ?\u0001(?: ?, ?\u0001)+ ?\)")]
    private static partial Regex ParameterList();

    [GeneratedRegex(@"\(\u0001\)(?: ?, ?\(\u0001\))+")]
    private static partial Regex ParameterRows();
}
