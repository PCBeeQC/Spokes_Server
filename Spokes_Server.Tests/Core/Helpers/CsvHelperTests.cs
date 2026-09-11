using Spokes_Server.Core.Helpers;
using Xunit;

namespace Spokes_Server.Tests.Core.Helpers
{
    public class CsvHelperTests
    {
        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("NormalText", "NormalText")]
        [InlineData("Text with spaces", "Text with spaces")]
        public void EscapeCsv_ReturnsPlainString_WhenNoSpecialCharacters(string? input, string expected)
        {
            var result = CsvHelper.EscapeCsv(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("Hello,World", "\"Hello,World\"")]
        [InlineData("Quote \"here\"", "\"Quote \"\"here\"\"\"")]
        [InlineData("Line1\nLine2", "\"Line1\nLine2\"")]
        [InlineData("Line1\rLine2", "\"Line1\rLine2\"")]
        public void EscapeCsv_QuotesAndEscapes_WhenDelimitersPresent(string input, string expected)
        {
            var result = CsvHelper.EscapeCsv(input);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("=1+1")]
        [InlineData("+SUM(A1:A10)")]
        [InlineData("-100")]
        [InlineData("@evil")]
        [InlineData("\tTabPrefix")]
        [InlineData("=cmd|'/C calc'!A0")]
        public void EscapeCsv_SanitizesFormulaInjectionTriggers(string input)
        {
            var result = CsvHelper.EscapeCsv(input);
            Assert.True(result.StartsWith("'") || result.StartsWith("\"'"));
        }
    }
}
