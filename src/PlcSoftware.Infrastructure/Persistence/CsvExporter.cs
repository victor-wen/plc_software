using System.Text;

namespace PlcSoftware.Infrastructure.Persistence;

/// <summary>
/// Minimal, safe CSV encoder for the export surface (design §6.5). Two concerns are handled:
/// spreadsheet formula-injection neutralisation and RFC-4180 field quoting. It is a pure, static,
/// side-effect-free helper so it is trivially testable on its own.
/// </summary>
public static class CsvExporter
{
    private static readonly char[] FormulaPrefixes = { '=', '+', '-', '@' };

    /// <summary>
    /// Returns a <paramref name="field"/> that is safe to place in a CSV cell. Fields starting with a
    /// spreadsheet formula prefix (<c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>) are neutralised by prepending
    /// a single quote. Fields containing a double quote, comma or newline are wrapped in double quotes
    /// with internal double quotes doubled. Everything else is returned verbatim.
    /// </summary>
    public static string Escape(string field)
    {
        ArgumentNullException.ThrowIfNull(field);

        var value = field.Length > 0 && Array.IndexOf(FormulaPrefixes, field[0]) >= 0
            ? "'" + field
            : field;

        var needsQuoting = value.IndexOf('"') >= 0 || value.IndexOf(',') >= 0 ||
                           value.IndexOf('\n') >= 0 || value.IndexOf('\r') >= 0;

        if (!needsQuoting)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var ch in value)
        {
            if (ch == '"')
            {
                builder.Append("\"\"");
            }
            else
            {
                builder.Append(ch);
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>
    /// Writes <paramref name="fields"/> as a single CSV row: each field escaped via <see cref="Escape"/>,
    /// joined with commas, terminated by <see cref="TextWriter.NewLine"/>.
    /// </summary>
    public static void WriteRow(TextWriter writer, IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(fields);

        var escaped = fields.Select(Escape);
        writer.WriteLine(string.Join(",", escaped));
    }
}
