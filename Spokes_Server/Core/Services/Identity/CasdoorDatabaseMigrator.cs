using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Spokes_Server.Core.Services.Identity;

public class CasdoorDatabaseMigrator
{
    private readonly ILogger<CasdoorDatabaseMigrator>? _logger;

    public CasdoorDatabaseMigrator(ILogger<CasdoorDatabaseMigrator>? logger = null)
    {
        _logger = logger;
    }

    public async Task<int> UpdateAdminCredentialsAndBrandingAsync(
        CasdoorBrandingMigrationOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(options.DbPath)) return 0;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DbPath, DefaultTimeout = 30 }.ConnectionString);
                await connection.OpenAsync(cancellationToken);

                // Ensure all init_data.json records (users, applications, organization) have been seeded by Casdoor
                // before executing UPDATE statements. Running UPDATEs before InitFromFile() finishes inserting 'spokes'
                // would update only 'built-in' rows and leave 'spokes' with Casdoor's auto-generated random client_id.
                using (var readinessCmd = connection.CreateCommand())
                {
                    readinessCmd.CommandText = @"
                        SELECT
                            (SELECT COUNT(1) FROM `user` WHERE name = $builtInAdmin AND owner = 'built-in') +
                            (SELECT COUNT(1) FROM `user` WHERE name = $spokesAdmin AND owner = 'spokes-org') +
                            (SELECT COUNT(1) FROM `application` WHERE name = 'app-built-in') +
                            (SELECT COUNT(1) FROM `application` WHERE name = 'spokes') +
                            (SELECT COUNT(1) FROM `organization` WHERE name = 'spokes-org')";
                    readinessCmd.Parameters.AddWithValue("$builtInAdmin", options.BuiltInUsername);
                    readinessCmd.Parameters.AddWithValue("$spokesAdmin", options.SpokesUsername);

                    var seededCount = Convert.ToInt32(await readinessCmd.ExecuteScalarAsync(cancellationToken));
                    if (seededCount < 5)
                    {
                        return 0;
                    }
                }

                var command = connection.CreateCommand();
                command.CommandText = "UPDATE `user` SET password = $password, password_type = 'bcrypt', password_salt = $salt WHERE name = $adminName AND owner = 'built-in'";
                command.Parameters.AddWithValue("$password", options.HashedBuiltInPassword);
                command.Parameters.AddWithValue("$adminName", options.BuiltInUsername);
                command.Parameters.AddWithValue("$salt", string.Empty);

                var rows = await command.ExecuteNonQueryAsync(cancellationToken);

                var command2 = connection.CreateCommand();
                command2.CommandText = "UPDATE `user` SET password = $password, password_type = 'bcrypt', password_salt = $salt WHERE name = $adminName AND owner = 'spokes-org'";
                command2.Parameters.AddWithValue("$password", options.HashedSpokesPassword);
                command2.Parameters.AddWithValue("$adminName", options.SpokesUsername);
                command2.Parameters.AddWithValue("$salt", string.Empty);

                rows += await command2.ExecuteNonQueryAsync(cancellationToken);

                var patchAppCommand = connection.CreateCommand();
                patchAppCommand.CommandText = "UPDATE `application` SET enable_sign_up = 0, display_name = $cmpName, logo = $logoUrl, favicon = $faviconUrl, enable_auto_signin = 1 WHERE name = 'app-built-in'";
                patchAppCommand.Parameters.AddWithValue("$cmpName", "Casdoor Admin");
                patchAppCommand.Parameters.AddWithValue("$logoUrl", options.LogoUrl);
                patchAppCommand.Parameters.AddWithValue("$faviconUrl", options.FaviconUrl);
                rows += await patchAppCommand.ExecuteNonQueryAsync(cancellationToken);

                var patchSpokesAppCommand = connection.CreateCommand();
                patchSpokesAppCommand.CommandText = "UPDATE `application` SET enable_sign_up = 0, display_name = $cmpName, logo = $logoUrl, favicon = $faviconUrl, enable_auto_signin = 1, client_id = $clientId, client_secret = $clientSecret WHERE name = 'spokes'";
                patchSpokesAppCommand.Parameters.AddWithValue("$cmpName", options.CompanyName);
                patchSpokesAppCommand.Parameters.AddWithValue("$logoUrl", options.LogoUrl);
                patchSpokesAppCommand.Parameters.AddWithValue("$faviconUrl", options.FaviconUrl);
                patchSpokesAppCommand.Parameters.AddWithValue("$clientId", options.ClientId);
                patchSpokesAppCommand.Parameters.AddWithValue("$clientSecret", options.ClientSecret);
                var spokesAppRows = await patchSpokesAppCommand.ExecuteNonQueryAsync(cancellationToken);
                if (spokesAppRows == 0)
                {
                    return 0;
                }
                rows += spokesAppRows;

                var patchOrgCommand = connection.CreateCommand();
                patchOrgCommand.CommandText = "UPDATE `organization` SET has_privilege_consent = 1, logo = $fixedLogo, favicon = $fixedFavicon WHERE name = 'spokes-org'";
                patchOrgCommand.Parameters.AddWithValue("$fixedLogo", options.FixedLogoUrl);
                patchOrgCommand.Parameters.AddWithValue("$fixedFavicon", options.FixedFaviconUrl);
                rows += await patchOrgCommand.ExecuteNonQueryAsync(cancellationToken);

                _logger?.LogInformation("[CasdoorDatabaseMigrator] Updated {Rows} records for credentials and branding in SQLite.", rows);
                return rows;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                // Tables are still being created by Casdoor; let caller poll again.
                return 0;
            }
            catch (SqliteException ex) when (attempt < 4)
            {
                _logger?.LogWarning(ex, "[CasdoorDatabaseMigrator] SQLite database busy/locked (attempt {Attempt}/5). Retrying...", attempt + 1);
                await Task.Delay(500 * (attempt + 1), cancellationToken);
            }
        }

        return 0;
    }

    public async Task<bool> SanitizeOrganizationAsync(
        CasdoorOrganizationSanitizationOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(options.DbPath)) return false;

        for (int i = 0; i < options.MaxRetries; i++)
        {
            try
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = options.DbPath, DefaultTimeout = 30 }.ConnectionString);
                await connection.OpenAsync(cancellationToken);

                var getOrgCmd = connection.CreateCommand();
                getOrgCmd.CommandText = "SELECT account_items, nav_items, widget_items FROM `organization` WHERE name = 'spokes-org'";
                using var reader = await getOrgCmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    var accountItemsStr = reader.IsDBNull(0) ? "[]" : reader.GetString(0);
                    var navItemsStr = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
                    var widgetItemsStr = reader.IsDBNull(2) ? "[]" : reader.GetString(2);

                    var accountItems = JsonNode.Parse(accountItemsStr)?.AsArray();
                    var navItems = JsonNode.Parse(navItemsStr)?.AsArray();
                    var widgetItems = JsonNode.Parse(widgetItemsStr)?.AsArray();

                    // Account Items - Disable everything except essential fields
                    if (accountItems != null)
                    {
                        var keepItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        {
                            "Organization", "ID", "Name", "Display name",
                            "User type", "Password", "Email", "Is admin", "Avatar"
                        };

                        foreach (var item in accountItems)
                        {
                            if (item is null) continue;
                            var itemName = item["name"]?.ToString();
                            if (itemName != null && !keepItems.Contains(itemName))
                            {
                                item["visible"] = false;
                            }
                        }
                    }

                    // Explicitly define standard Nav Items using exact paths
                    var standardNavItems = new[] {
                        "/home-top", "/orgs-top", "/applications-top", "/roles-top", "/sessions-top",
                        "/admin-top", "/", "/shortcuts", "/apps", "/organizations", "/groups", "/users",
                        "/invitations", "/applications", "/providers", "/resources", "/certs", "/roles",
                        "/permissions", "/models", "/adapters", "/enforcers", "/sessions", "/records",
                        "/tokens", "/verifications", "/sysinfo", "/syncers", "/webhooks", "/webhook-events", "/swagger"
                    };
                    navItems = new JsonArray();
                    foreach (var navItem in standardNavItems)
                    {
                        navItems.Add(navItem);
                    }

                    // Explicitly define standard Widget Items using exact paths
                    var standardWidgetItems = new[] {
                        "tour", "language", "theme"
                    };
                    widgetItems = new JsonArray();
                    foreach (var widgetItem in standardWidgetItems)
                    {
                        widgetItems.Add(widgetItem);
                    }

                    var updateOrgCmd = connection.CreateCommand();
                    updateOrgCmd.CommandText = "UPDATE `organization` SET display_name = $displayName, logo = $logo, favicon = $favicon, has_privilege_consent = 1, website_url = 'https://www.spokes.sh', default_avatar = 'https://cdn.casbin.org/img/casbin.svg', account_items = $accountItems, nav_items = $navItems, widget_items = $widgetItems WHERE name = 'spokes-org'";
                    updateOrgCmd.Parameters.AddWithValue("$displayName", options.CompanyName);
                    updateOrgCmd.Parameters.AddWithValue("$logo", options.FixedLogoUrl);
                    updateOrgCmd.Parameters.AddWithValue("$favicon", options.FixedFaviconUrl);
                    updateOrgCmd.Parameters.AddWithValue("$accountItems", accountItems?.ToJsonString() ?? "[]");
                    updateOrgCmd.Parameters.AddWithValue("$navItems", navItems?.ToJsonString() ?? "[]");
                    updateOrgCmd.Parameters.AddWithValue("$widgetItems", widgetItems?.ToJsonString() ?? "[]");

                    await updateOrgCmd.ExecuteNonQueryAsync(cancellationToken);

                    // Force enable signups on the Spokes application during one-time init
                    var updateAppCmd = connection.CreateCommand();
                    updateAppCmd.CommandText = "UPDATE `application` SET enable_sign_up = 1 WHERE name = 'spokes'";
                    await updateAppCmd.ExecuteNonQueryAsync(cancellationToken);

                    _logger?.LogInformation("[CasdoorDatabaseMigrator] First-run sanitization applied to Casdoor SQLite database.");
                    return true;
                }

                return false;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
            {
                if (i == options.MaxRetries - 1)
                {
                    _logger?.LogWarning("[CasdoorDatabaseMigrator] Failed to sync database due to SQLITE_BUSY after {MaxRetries} retries.", options.MaxRetries);
                    return false;
                }

                await Task.Delay(1000, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[CasdoorDatabaseMigrator] Failed to sync database directly");
                return false;
            }
        }

        return false;
    }
}
