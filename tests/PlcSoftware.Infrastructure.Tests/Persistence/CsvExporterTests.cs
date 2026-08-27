using PlcSoftware.Infrastructure.Persistence;

namespace PlcSoftware.Infrastructure.Tests.Persistence;

/// <summary>
/// Behavioural tests for the CSV field encoder (design §6.5 export surface).
///
/// Verified rules:
///   - a field starting with <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> is neutralised by prepending a
///     single quote (spreadsheet formula-injection protection);
///   - a field containing a double quote, comma or newline is wrapped in double quotes with internal
///     double quotes doubled;
///   - plain fields (including Chinese text, which carries no formula/metacharacters) are unmolested;
///   - <see cref="CsvExporter.WriteRow"/> joins escaped fields with commas and appends a newline.
/// </summary>
public class CsvExporterTests
{
    [Theory]
    [InlineData("屏蔽 光栅 参数 调试")]
    [InlineData("hello")]
    [InlineData("K1 急停")]
    public void Escape_PlainField_IsUnchanged(string input)
    {
        Assert.Equal(input, CsvExporter.Escape(input));
    }

    [Fact]
    public void Escape_FormulaPrefix_PrependsSingleQuote()
    {
        Assert.Equal("'=SUM(A1:A2)", CsvExporter.Escape("=SUM(A1:A2)"));
        Assert.Equal("'+1", CsvExporter.Escape("+1"));
        Assert.Equal("'-1", CsvExporter.Escape("-1"));
        Assert.Equal("'@cmd", CsvExporter.Escape("@cmd"));
    }

    [Fact]
    public void Escape_ContainsComma_WrapsInDoubleQuotes()
    {
        Assert.Equal("\"a,b\"", CsvExporter.Escape("a,b"));
    }

    [Fact]
    public void Escape_ContainsNewline_WrapsInDoubleQuotes()
    {
        Assert.Equal("\"line1\nline2\"", CsvExporter.Escape("line1\nline2"));
    }

    [Fact]
    public void Escape_ContainsQuote_DoublesInternalQuotes()
    {
        Assert.Equal("\"say \"\"hi\"\"\"", CsvExporter.Escape("say \"hi\""));
    }

    [Fact]
    public void Escape_FormulaPrefixWithComma_PrependsQuoteThenWraps()
    {
        Assert.Equal("\"'=1,2\"", CsvExporter.Escape("=1,2"));
    }

    [Fact]
    public void WriteRow_JoinsEscapedFieldsWithCommasAndNewline()
    {
        using var writer = new StringWriter();

        CsvExporter.WriteRow(writer, new[] { "plain", "a,b", "=SUM(A1:A2)", "\"q\"", "中" });

        var expected = $"plain,\"a,b\",'=SUM(A1:A2),\"\"\"q\"\"\",中{Environment.NewLine}";
        Assert.Equal(expected, writer.ToString());
    }
}
