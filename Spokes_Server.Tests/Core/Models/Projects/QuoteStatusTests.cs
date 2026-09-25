namespace Spokes_Server.Tests.Core.Models.Projects;

using System.Reflection;
using Spokes_Server.Core.Models.Projects;

public class QuoteStatusTests
{
        [Fact]
        public void Draft_HasExpectedValue()
        {
            Assert.Equal("Draft", QuoteStatus.Draft);
        }

        [Fact]
        public void Final_HasExpectedValue()
        {
            Assert.Equal("Final", QuoteStatus.Final);
        }

        [Fact]
        public void Accepted_HasExpectedValue()
        {
            Assert.Equal("Accepted", QuoteStatus.Accepted);
        }

        [Fact]
        public void Rejected_HasExpectedValue()
        {
            Assert.Equal("Rejected", QuoteStatus.Rejected);
        }

        [Theory]
        [InlineData(QuoteStatus.Draft, "Draft")]
        [InlineData(QuoteStatus.Final, "Final")]
        [InlineData(QuoteStatus.Accepted, "Accepted")]
        [InlineData(QuoteStatus.Rejected, "Rejected")]
        public void QuoteStatus_Constants_MatchExpectedString(string actual, string expected)
        {
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void QuoteStatus_AllConstants_AreUniqueAndNonEmpty()
        {
            var fields = typeof(QuoteStatus)
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
                .Select(f => (string)f.GetValue(null)!)
                .ToList();

            Assert.Equal(4, fields.Count);
            Assert.All(fields, val => Assert.False(string.IsNullOrWhiteSpace(val)));
            Assert.Equal(fields.Distinct().Count(), fields.Count);
        }
    }
