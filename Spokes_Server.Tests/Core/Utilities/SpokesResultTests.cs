using Spokes_Server.Core.Utilities;

namespace Spokes_Server.Tests.Core.Utilities
{
    public class SpokesResultTests
    {
        [Fact]
        public void Success_GenericWithValue_SetsExpectedProperties()
        {
            // Arrange
            const string expectedValue = "Hello World";

            // Act
            var result = SpokesResult<string>.Success(expectedValue);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.Equal(expectedValue, result.Value);
            Assert.Null(result.ErrorMessage);
            Assert.Null(result.Exception);
        }

        [Fact]
        public void Success_GenericWithNullableValueNull_SetsExpectedProperties()
        {
            // Act
            var result = SpokesResult<string?>.Success(null);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.Null(result.Value);
            Assert.Null(result.ErrorMessage);
            Assert.Null(result.Exception);
        }

        [Fact]
        public void Failure_GenericWithErrorMessage_SetsExpectedProperties()
        {
            // Arrange
            const string errorMessage = "Something went wrong";

            // Act
            var result = SpokesResult<int>.Failure(errorMessage);

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Equal(default, result.Value);
            Assert.Equal(errorMessage, result.ErrorMessage);
            Assert.Null(result.Exception);
        }

        [Fact]
        public void Failure_GenericWithException_SetsExpectedPropertiesAndExceptionMessage()
        {
            // Arrange
            var exception = new InvalidOperationException("Failed operation");

            // Act
            var result = SpokesResult<string>.Failure(exception);

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Null(result.Value);
            Assert.Equal("Failed operation", result.ErrorMessage);
            Assert.Same(exception, result.Exception);
        }

        [Fact]
        public void Success_NonGeneric_SetsExpectedProperties()
        {
            // Act
            var result = SpokesResult.Success();

            // Assert
            Assert.True(result.IsSuccess);
            Assert.Null(result.ErrorMessage);
            Assert.Null(result.Exception);
        }

        [Fact]
        public void Failure_NonGenericWithErrorMessage_SetsExpectedProperties()
        {
            // Arrange
            const string errorMessage = "Action could not be completed";

            // Act
            var result = SpokesResult.Failure(errorMessage);

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Equal(errorMessage, result.ErrorMessage);
            Assert.Null(result.Exception);
        }

        [Fact]
        public void Failure_NonGenericWithException_SetsExpectedPropertiesAndExceptionMessage()
        {
            // Arrange
            var exception = new ArgumentNullException("param", "Argument was null");

            // Act
            var result = SpokesResult.Failure(exception);

            // Assert
            Assert.False(result.IsSuccess);
            Assert.Equal(exception.Message, result.ErrorMessage);
            Assert.Same(exception, result.Exception);
        }
    }
}
