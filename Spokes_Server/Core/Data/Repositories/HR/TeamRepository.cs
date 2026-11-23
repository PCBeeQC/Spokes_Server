using System;
using System.Collections.Generic;
using Spokes_Server.Core.Models.HR;

namespace Spokes_Server.Core.Data.Repositories.HR;

public class TeamRepository : JsonRepository<Team>
{
    public TeamRepository(DiskPersistenceService writer, IConfiguration config)
        : base(writer, Path.Combine(config["DataPath"] ?? "Data", "Teams"))
    {
    }

    protected override string GetFilePath(Team entity) =>
        Path.Combine(_basePath, $"{entity.Id}.json");

    /// <summary>
    /// Collects all team IDs associated with <paramref name="employeeId"/> via direct membership or leadership.
    /// </summary>
    public static HashSet<string> GetUserTeamIds(TeamRepository? teams, string? employeeId, Employee? employee)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(employeeId)) return result;

        if (!string.IsNullOrWhiteSpace(employee?.TeamId))
        {
            result.Add(employee.TeamId);
        }

        if (teams != null)
        {
            foreach (var team in teams.GetAll())
            {
                if (string.Equals(team.LeaderId, employeeId, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(team.Id);
                }
            }
        }

        return result;
    }
}
