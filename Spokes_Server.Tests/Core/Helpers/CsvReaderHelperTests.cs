using System.IO;
using System.Text;
using Spokes_Server.Core.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Helpers;

public class CsvReaderHelperTests
{
    [Fact]
    public void Parse_EmptyOrNullString_ReturnsEmptyList()
    {
        Assert.Empty(CsvReaderHelper.Parse(""));
        Assert.Empty(CsvReaderHelper.Parse((string)null!));
    }

    [Fact]
    public void Parse_SimpleCsv_ReturnsExpectedRowsAndColumns()
    {
        var csv = "A,B,C\r\n1,2,3\r\n4,5,6";
        var rows = CsvReaderHelper.Parse(csv);

        Assert.Equal(3, rows.Count);
        Assert.Equal(["A", "B", "C"], rows[0]);
        Assert.Equal(["1", "2", "3"], rows[1]);
        Assert.Equal(["4", "5", "6"], rows[2]);
    }

    [Fact]
    public void Parse_QuotedFieldWithComma_PreservesCommaInsideField()
    {
        var csv = "Name,Address\r\n\"Smith, John\",\"123 Main St, Suite 400\"";
        var rows = CsvReaderHelper.Parse(csv);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Smith, John", rows[1][0]);
        Assert.Equal("123 Main St, Suite 400", rows[1][1]);
    }

    [Fact]
    public void Parse_QuotedFieldWithEscapedQuotes_UnescapesQuotes()
    {
        var csv = "ID,Message\r\n1,\"He said \"\"Hello\"\" to everyone\"";
        var rows = CsvReaderHelper.Parse(csv);

        Assert.Equal(2, rows.Count);
        Assert.Equal("He said \"Hello\" to everyone", rows[1][1]);
    }

    [Fact]
    public void Parse_MultilineQuotedField_PreservesNewlines()
    {
        var csv = "ID,Description\r\n101,\"Line 1\r\nLine 2\r\nLine 3\"\r\n102,Single Line";
        var rows = CsvReaderHelper.Parse(csv);

        Assert.Equal(3, rows.Count);
        Assert.Equal("101", rows[1][0]);
        Assert.Equal("Line 1\r\nLine 2\r\nLine 3", rows[1][1]);
        Assert.Equal("102", rows[2][0]);
        Assert.Equal("Single Line", rows[2][1]);
    }

    [Fact]
    public void Parse_MixedLineEndings_HandlesBothCrlfAndLf()
    {
        var csv = "Col1,Col2\nRow1A,Row1B\r\nRow2A,Row2B\nRow3A,Row3B";
        var rows = CsvReaderHelper.Parse(csv);

        Assert.Equal(4, rows.Count);
        Assert.Equal(["Col1", "Col2"], rows[0]);
        Assert.Equal(["Row1A", "Row1B"], rows[1]);
        Assert.Equal(["Row2A", "Row2B"], rows[2]);
        Assert.Equal(["Row3A", "Row3B"], rows[3]);
    }

    [Fact]
    public void Parse_TrailingEmptyFields_PreservesEmptyEntries()
    {
        var csv = "A,B,C,\r\n1,2,,";
        var rows = CsvReaderHelper.Parse(csv);

        Assert.Equal(2, rows.Count);
        Assert.Equal(4, rows[0].Count);
        Assert.Equal("", rows[0][3]);
        Assert.Equal(4, rows[1].Count);
        Assert.Equal("", rows[1][2]);
        Assert.Equal("", rows[1][3]);
    }

    [Fact]
    public void Parse_FromTextReaderStream_WorksIdentically()
    {
        var csv = "Header1,Header2\r\nVal1,Val2";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        using var reader = new StreamReader(stream);

        var rows = CsvReaderHelper.Parse(reader);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Val1", rows[1][0]);
        Assert.Equal("Val2", rows[1][1]);
    }
}
