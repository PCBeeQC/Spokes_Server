using System;
using System.Collections.Generic;

namespace Spokes_Server.Core.Services.Migrations;

public class MigrationRecord
{
    public string Key { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime AppliedAtUtc { get; set; }
}

public class MigrationHistoryLedger
{
    public List<MigrationRecord> AppliedMigrations { get; set; } = [];
}

public class MigrationRunSummary
{
    public int TotalSteps { get; set; }
    public int ExecutedSteps { get; set; }
    public int SkippedSteps { get; set; }
    public List<string> AppliedKeys { get; set; } = [];
}
