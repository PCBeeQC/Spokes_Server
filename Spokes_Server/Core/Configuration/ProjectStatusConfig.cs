namespace Spokes_Server.Core.Configuration;

using System;
using System.Collections.Generic;

public static class ProjectStatus
{
    public const string Draft = "Draft";
    public const string Quoted = "Quoted";
    public const string InProduction = "Quote Accepted / In Production";
    public const string WaitingForPayment = "Waiting for last payment";
    public const string Discovery = "Discovery";
    public const string OnHold = "On Hold";
    public const string Completed = "Completed";
    public const string Archived = "Archived";

    public static List<string> All =>
    [
        Draft, Quoted, InProduction, WaitingForPayment, Discovery, OnHold, Completed, Archived
    ];
}

public class ProjectStatusConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "Default"; // MudBlazor Color Enum as string
    public bool IsSystemDefault { get; set; } // Cannot be renamed/deleted if true (optional, but good practice)
    public string SystemMapping { get; set; } = SystemStatusMappings.Standard;
}

public static class SystemStatusMappings
{
    public const string Standard = "Standard";
    public const string Completed = "Completed";
    public const string Archived = "Archived";

    public static List<string> All => [Standard, Completed, Archived];
}
