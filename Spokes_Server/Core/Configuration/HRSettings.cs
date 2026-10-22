namespace Spokes_Server.Core.Configuration;

public class HRSettings
{
    public bool EnableHourBank { get; set; } = true;
    public string OvertimeCalculationMode { get; set; } = "Weekly"; // "Weekly", "Daily", "None"
    public decimal DailyOvertimeThreshold { get; set; } = 8m; 
    public decimal MaxBankableHours { get; set; } = 0; // 0 = unlimited
}
