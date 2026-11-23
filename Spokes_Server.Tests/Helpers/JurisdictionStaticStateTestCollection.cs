namespace Spokes_Server.Tests.Helpers;

/// <summary>
/// Serializes tests that mutate process-wide static jurisdiction state
/// (<see cref="Spokes_Server.Core.Services.Accounting.FinancialCalculationEngine.ActiveTemplate"/>)
/// so parallel xUnit collections cannot overwrite active tax rules mid-test.
/// </summary>
[CollectionDefinition("JurisdictionStaticState", DisableParallelization = true)]
public sealed class JurisdictionStaticStateTestCollection
{
}
