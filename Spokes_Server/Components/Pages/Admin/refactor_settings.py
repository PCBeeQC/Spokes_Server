import re
import os

filepath = r"c:\Users\MatsDallaire\Documents\GitHub\Spokes\Spokes_Server\Spokes_Server\Components\Pages\Admin\CompanySettings.razor"
with open(filepath, 'r', encoding='utf-8') as f:
    content = f.read()

# 1. Update Menu
menu_old = """            <MudMenuItem OnClick="@(() => UpdateActiveTab(3))" Icon="@Icons.Material.Filled.Category">Calendar Categories</MudMenuItem>
            <MudMenuItem OnClick="@(() => UpdateActiveTab(4))" Icon="@Icons.Material.Filled.Notifications">Push Notifications</MudMenuItem>
            <MudMenuItem OnClick="@(() => UpdateActiveTab(5))" Icon="@Icons.Material.Filled.Backup">Backups & Restore</MudMenuItem>
            <MudMenuItem OnClick="@(() => UpdateActiveTab(7))" Icon="@Icons.Material.Filled.Security">Permission Groups</MudMenuItem>
            @if (!IsFriendEdition)
            {
                <MudMenuItem OnClick="@(() => UpdateActiveTab(6))" Icon="@Icons.Material.Filled.Email">Email Server</MudMenuItem>
            }
            <MudMenuItem OnClick="@(() => UpdateActiveTab(8))" Icon="@Icons.Material.Filled.Chat">Chat Settings</MudMenuItem>
            <MudMenuItem OnClick="@(() => UpdateActiveTab(9))" Icon="@Icons.Material.Filled.Key">Licensing</MudMenuItem>
            <MudMenuItem OnClick="@(() => UpdateActiveTab(10))" Icon="@Icons.Material.Filled.SettingsApplications">System Setup</MudMenuItem>"""

menu_new = """            <MudMenuItem OnClick="@(() => UpdateActiveTab(3))" Icon="@Icons.Material.Filled.Category">Calendar Categories</MudMenuItem>
            <MudMenuItem OnClick="@(() => UpdateActiveTab(4))" Icon="@Icons.Material.Filled.Backup">Backups & Restore</MudMenuItem>
            <MudMenuItem OnClick="@(() => UpdateActiveTab(5))" Icon="@Icons.Material.Filled.Security">Permission Groups</MudMenuItem>
            @if (!IsFriendEdition)
            {
                <MudMenuItem OnClick="@(() => UpdateActiveTab(6))" Icon="@Icons.Material.Filled.Email">Email Server</MudMenuItem>
            }
            <MudMenuItem OnClick="@(() => UpdateActiveTab(7))" Icon="@Icons.Material.Filled.Chat">Chat Settings</MudMenuItem>
            <MudMenuItem OnClick="@(() => UpdateActiveTab(8))" Icon="@Icons.Material.Filled.SettingsApplications">System Setup</MudMenuItem>"""
content = content.replace(menu_old, menu_new)

# 2. Extract contents of activeTabIndex == 4 (Push Notifications)
push_notif_start = "    else if (activeTabIndex == 4)\n    {\n        <MudGrid>"
push_notif_end = "    else if (activeTabIndex == 5)"
push_notif_block = content[content.find(push_notif_start):content.find(push_notif_end)]
# strip out the wrapping if and MudGrid
push_notif_inner = push_notif_block.replace("    else if (activeTabIndex == 4)\n    {\n        <MudGrid>\n", "").replace("\n        </MudGrid>\n    }\n", "")

# 3. Extract contents of activeTabIndex == 9 (Licensing)
licensing_start = "    else if (activeTabIndex == 9)\n    {\n        <MudGrid>"
licensing_end = "    else if (activeTabIndex == 10)"
licensing_block = content[content.find(licensing_start):content.find(licensing_end)]
# strip out the wrapping if and MudGrid
licensing_inner = licensing_block.replace("    else if (activeTabIndex == 9)\n    {\n        <MudGrid>\n", "").replace("\n        </MudGrid>\n    }\n", "")

# 4. Remove activeTabIndex == 4 and 9 blocks from content, and renumber the rest
# First let's remove them directly to not mess up the rest, but we also need to change the if statements
# We can do a string replace for the if statements

content = content.replace(push_notif_block, "")
content = content.replace(licensing_block, "")

# Renumber the if statements
content = content.replace("else if (activeTabIndex == 5)", "else if (activeTabIndex == 4)")
content = content.replace("else if (activeTabIndex == 7)", "else if (activeTabIndex == 5)") # Permissions was 7
content = content.replace("else if (activeTabIndex == 6)", "else if (activeTabIndex == 6)") # Email stays 6
content = content.replace("else if (activeTabIndex == 8)", "else if (activeTabIndex == 7)")
content = content.replace("else if (activeTabIndex == 10)", "else if (activeTabIndex == 8)")

# 5. Append the extracted content to the end of the new activeTabIndex == 8 (System Setup)
setup_end = "\n        </MudGrid>\n    }\n    </MudContainer>"

combined_inner = f"""
            <MudItem xs="12">
                <MudDivider Class="my-4" />
            </MudItem>
{licensing_inner}
            <MudItem xs="12">
                <MudDivider Class="my-4" />
            </MudItem>
{push_notif_inner}
"""

content = content.replace(setup_end, combined_inner + setup_end)

# 6. Update C# mapping blocks
param_set_old = """                "profile" => 0,
                "templates" => 1,
                "statuses" => 2,
                "categories" => 3,
                "notifications" => 4,
                "backups" => 5,
                "email" => 6,
                "permissions" => 7,
                "chat" => 8,
                "licensing" => 9,
                "setup" => 10,
                _ => 0"""

param_set_new = """                "profile" => 0,
                "templates" => 1,
                "statuses" => 2,
                "categories" => 3,
                "backups" => 4,
                "permissions" => 5,
                "email" => 6,
                "chat" => 7,
                "setup" => 8,
                _ => 0"""
content = content.replace(param_set_old, param_set_new)

tab_name_old = """            0 => "profile",
            1 => "templates",
            2 => "statuses",
            3 => "categories",
            4 => "notifications",
            5 => "backups",
            6 => "email",
            7 => "permissions",
            8 => "chat",
            9 => "licensing",
            10 => "setup",
            _ => "profile" """

tab_name_new = """            0 => "profile",
            1 => "templates",
            2 => "statuses",
            3 => "categories",
            4 => "backups",
            5 => "permissions",
            6 => "email",
            7 => "chat",
            8 => "setup",
            _ => "profile" """
# Note: trailing space in string literal to match correctly might be tricky, let's use regex or more precise matching.
# Actually, the string in code is `_ => "profile"` without trailing space in some lines.

tab_name_old_exact = """            0 => "profile",
            1 => "templates",
            2 => "statuses",
            3 => "categories",
            4 => "notifications",
            5 => "backups",
            6 => "email",
            7 => "permissions",
            8 => "chat",
            9 => "licensing",
            10 => "setup",
            _ => "profile" """

tab_name_new_exact = """            0 => "profile",
            1 => "templates",
            2 => "statuses",
            3 => "categories",
            4 => "backups",
            5 => "permissions",
            6 => "email",
            7 => "chat",
            8 => "setup",
            _ => "profile" """

if tab_name_old_exact.strip() in content:
    content = content.replace(tab_name_old_exact.strip(), tab_name_new_exact.strip())
else:
    # try another way
    content = re.sub(r'0 => "profile",\s*1 => "templates",\s*2 => "statuses",\s*3 => "categories",\s*4 => "notifications",\s*5 => "backups",\s*6 => "email",\s*7 => "permissions",\s*8 => "chat",\s*9 => "licensing",\s*10 => "setup",\s*_ => "profile"',
                     tab_name_new_exact.strip(), content)

get_active_name_old = """        0 => $"{AppLabels.Company} Profile",
        1 => "Document Templates",
        2 => "Project Statuses",
        3 => "Calendar Categories",
        4 => "Push Notifications",
        5 => "Backups & Restore",
        6 => "Email Server",
        7 => "Permission Groups",
        8 => "Chat Settings",
        9 => "Licensing",
        10 => "System Setup",
        _ => $"{AppLabels.Company} Profile\""""
get_active_name_new = """        0 => $"{AppLabels.Company} Profile",
        1 => "Document Templates",
        2 => "Project Statuses",
        3 => "Calendar Categories",
        4 => "Backups & Restore",
        5 => "Permission Groups",
        6 => "Email Server",
        7 => "Chat Settings",
        8 => "System Setup",
        _ => $"{AppLabels.Company} Profile\""""
content = re.sub(r'0 => \$\"\{AppLabels\.Company\} Profile\",\s*1 => "Document Templates",\s*2 => "Project Statuses",\s*3 => "Calendar Categories",\s*4 => "Push Notifications",\s*5 => "Backups & Restore",\s*6 => "Email Server",\s*7 => "Permission Groups",\s*8 => "Chat Settings",\s*9 => "Licensing",\s*10 => "System Setup",\s*_ => \$\"\{AppLabels\.Company\} Profile\"', get_active_name_new.strip(), content)


get_active_icon_old = """        0 => Icons.Material.Filled.Business,
        1 => Icons.Material.Filled.Description,
        2 => Icons.Material.Filled.Palette,
        3 => Icons.Material.Filled.Category,
        4 => Icons.Material.Filled.Notifications,
        5 => Icons.Material.Filled.Backup,
        6 => Icons.Material.Filled.Email,
        7 => Icons.Material.Filled.Security,
        8 => Icons.Material.Filled.Chat,
        9 => Icons.Material.Filled.Key,
        10 => Icons.Material.Filled.SettingsApplications,"""
get_active_icon_new = """        0 => Icons.Material.Filled.Business,
        1 => Icons.Material.Filled.Description,
        2 => Icons.Material.Filled.Palette,
        3 => Icons.Material.Filled.Category,
        4 => Icons.Material.Filled.Backup,
        5 => Icons.Material.Filled.Security,
        6 => Icons.Material.Filled.Email,
        7 => Icons.Material.Filled.Chat,
        8 => Icons.Material.Filled.SettingsApplications,"""
# Since there is `_ => Icons.Material.Filled.Business` below it, we just replace the above part.
content = re.sub(r'0 => Icons\.Material\.Filled\.Business,\s*1 => Icons\.Material\.Filled\.Description,\s*2 => Icons\.Material\.Filled\.Palette,\s*3 => Icons\.Material\.Filled\.Category,\s*4 => Icons\.Material\.Filled\.Notifications,\s*5 => Icons\.Material\.Filled\.Backup,\s*6 => Icons\.Material\.Filled\.Email,\s*7 => Icons\.Material\.Filled\.Security,\s*8 => Icons\.Material\.Filled\.Chat,\s*9 => Icons\.Material\.Filled\.Key,\s*10 => Icons\.Material\.Filled\.SettingsApplications,', get_active_icon_new.strip() + ",", content)

with open(filepath, 'w', encoding='utf-8') as f:
    f.write(content)

print("Refactor complete.")
