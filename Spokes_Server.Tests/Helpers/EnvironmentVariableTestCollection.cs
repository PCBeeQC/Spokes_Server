namespace Spokes_Server.Tests.Helpers;

/// <summary>
/// Serializes tests that mutate process-wide environment variables (such as SPOKES_MASTER_PASSWORD)
/// so they execute after parallel test collections without racing against concurrent tests.
/// </summary>
[CollectionDefinition("EnvironmentVariableTests", DisableParallelization = true)]
public sealed class EnvironmentVariableTestCollection
{
}
