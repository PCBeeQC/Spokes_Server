<#
.SYNOPSIS
    Extracts localization keys from .razor and .cs files into AppResources.resx (Family/Shared)
    and BusinessResources.resx (Business Edition).
.DESCRIPTION
    Scans the Spokes_Server codebase for localization key references:
      - @Loc["..."] / Loc["..."]       -> Resources/AppResources.resx (synced with Crowdin)
      - @BizLoc["..."] / BizLoc["..."] -> Resources/BusinessResources.resx (in-repo only, not on Crowdin)
    
    When -Prune is specified, it removes orphaned keys from .resx files (and satellites) that are
    no longer referenced in code.
    When -CheckOnly is specified, checks for missing/orphaned keys without modifying files.
#>
[CmdletBinding()]
param(
    [string]$ServerDir = $PSScriptRoot,
    [switch]$CheckOnly,
    [switch]$Prune
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ServerDir)) {
    $ServerDir = $PSScriptRoot
}
if ([string]::IsNullOrWhiteSpace($ServerDir)) {
    $ServerDir = "."
}

$resourcesDir = Join-Path $ServerDir "Resources"
if (-not (Test-Path $resourcesDir)) {
    throw "Resources directory not found at: $resourcesDir"
}

# Save formatted XML with UTF-8 encoding
function Save-ResxXml([xml]$xmlDoc, [string]$filePath) {
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Indent = $true
    $settings.IndentChars = "  "
    $settings.Encoding = [System.Text.Encoding]::UTF8
    $settings.OmitXmlDeclaration = $false

    $writer = [System.Xml.XmlWriter]::Create($filePath, $settings)
    try {
        $xmlDoc.Save($writer)
    }
    finally {
        $writer.Dispose()
    }
}

# Scan all .razor and .cs files (ignoring build artifacts and Resources)
$searchFiles = Get-ChildItem -Path $ServerDir -Recurse -Include "*.razor", "*.cs" | Where-Object {
    $_.FullName -notmatch "[\\/](bin|obj|\.git|\.vs|Resources)[\\/]"
}

function Process-LocalizationResource {
    param(
        [string]$ResourceBaseName,
        [string]$Pattern,
        [System.Collections.Generic.HashSet[string]]$SharedKeysToIgnore = $null
    )

    $resxPath = Join-Path $resourcesDir "$ResourceBaseName.resx"
    if (-not (Test-Path $resxPath)) {
        throw "$ResourceBaseName.resx not found at: $resxPath"
    }

    [xml]$resxXml = [System.IO.File]::ReadAllText($resxPath, [System.Text.Encoding]::UTF8)

    # Collect existing keys
    $existingKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($dataNode in $resxXml.root.data) {
        if ($dataNode.name) {
            $null = $existingKeys.Add($dataNode.name)
        }
    }

    Write-Host "`n=== Processing $ResourceBaseName ===" -ForegroundColor Cyan
    Write-Host "Found $($existingKeys.Count) existing keys in $ResourceBaseName.resx" -ForegroundColor Gray

    # Extract keys from code
    $foundKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($file in $searchFiles) {
        $content = [System.IO.File]::ReadAllText($file.FullName)
        $matches = [System.Text.RegularExpressions.Regex]::Matches($content, $Pattern)
        foreach ($match in $matches) {
            $key = $match.Groups[1].Value
            if (-not [string]::IsNullOrWhiteSpace($key)) {
                # If this key is already defined in shared common resource, skip it for business resource
                if ($SharedKeysToIgnore -and $SharedKeysToIgnore.Contains($key)) {
                    continue
                }
                $null = $foundKeys.Add($key)
            }
        }
    }

    Write-Host "Discovered $($foundKeys.Count) unique localization keys in code." -ForegroundColor Cyan

    # Check for potential case-insensitive collisions
    $seenLower = @{}
    foreach ($k in $foundKeys) {
        $lower = $k.ToLowerInvariant()
        if ($seenLower.ContainsKey($lower) -and $seenLower[$lower] -ne $k) {
            Write-Warning "[$ResourceBaseName] Case collision detected: '$k' vs '$($seenLower[$lower])'"
        } else {
            $seenLower[$lower] = $k
        }
    }

    $newKeys = $foundKeys | Where-Object { -not $existingKeys.Contains($_) } | Sort-Object
    $prunedKeys = @()

    if ($Prune) {
        $nodesToRemove = @()
        foreach ($dataNode in $resxXml.root.data) {
            if ($dataNode.name -and (-not $foundKeys.Contains($dataNode.name))) {
                $nodesToRemove += $dataNode
                $prunedKeys += $dataNode.name
            }
        }

        if ($nodesToRemove.Count -gt 0) {
            Write-Host "Found $($nodesToRemove.Count) orphaned keys to prune from $ResourceBaseName.resx:" -ForegroundColor Magenta
            $prunedKeys | ForEach-Object { Write-Host "  - $_" -ForegroundColor Magenta }

            if (-not $CheckOnly) {
                foreach ($node in $nodesToRemove) {
                    $null = $resxXml.root.RemoveChild($node)
                }
            }
        }
    }

    if ($newKeys.Count -gt 0) {
        Write-Host "Found $($newKeys.Count) new keys to append to $ResourceBaseName.resx:" -ForegroundColor Yellow
        $newKeys | ForEach-Object { Write-Host "  + $_" -ForegroundColor Yellow }
    }

    if ($newKeys.Count -eq 0 -and $prunedKeys.Count -eq 0) {
        Write-Host "All keys in $ResourceBaseName.resx are up-to-date. No changes needed." -ForegroundColor Green
    }

    if (-not $CheckOnly) {
        if ($newKeys.Count -gt 0) {
            foreach ($newKey in $newKeys) {
                $dataElem = $resxXml.CreateElement("data")
                $dataElem.SetAttribute("name", $newKey)
                $dataElem.SetAttribute("xml:space", "preserve")

                $valueElem = $resxXml.CreateElement("value")
                $valueElem.InnerText = $newKey
                $null = $dataElem.AppendChild($valueElem)

                $null = $resxXml.root.AppendChild($dataElem)
            }
        }

        if ($newKeys.Count -gt 0 -or $prunedKeys.Count -gt 0) {
            Save-ResxXml $resxXml $resxPath
            Write-Host "Saved updated $ResourceBaseName.resx (+$($newKeys.Count), -$($prunedKeys.Count))" -ForegroundColor Green
        }

        # Also prune and deduplicate satellite resx files if pruning
        if ($Prune) {
            $satelliteFiles = Get-ChildItem -Path $resourcesDir -Filter "$ResourceBaseName.*.resx"
            foreach ($satFile in $satelliteFiles) {
                [xml]$satXml = [System.IO.File]::ReadAllText($satFile.FullName, [System.Text.Encoding]::UTF8)
                $satNodesToRemove = @()
                $seenSatKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
                foreach ($dataNode in $satXml.root.data) {
                    if ($dataNode.name) {
                        if ((-not $foundKeys.Contains($dataNode.name)) -or ($seenSatKeys.Contains($dataNode.name))) {
                            $satNodesToRemove += $dataNode
                        } else {
                            $null = $seenSatKeys.Add($dataNode.name)
                        }
                    }
                }
                if ($satNodesToRemove.Count -gt 0) {
                    foreach ($node in $satNodesToRemove) {
                        $null = $satXml.root.RemoveChild($node)
                    }
                    Save-ResxXml $satXml $satFile.FullName
                    Write-Host "Pruned $($satNodesToRemove.Count) orphaned/duplicate keys from $($satFile.Name)" -ForegroundColor Green
                }
            }
        }
    }

    return [PSCustomObject]@{
        ResourceName = $ResourceBaseName
        FoundCount   = $foundKeys.Count
        NewCount     = $newKeys.Count
        PrunedCount  = $prunedKeys.Count
        ExistingKeys = $existingKeys
    }
}

# 1. Process Family Edition & Shared UI (AppResources.resx)
# Pattern matches @Loc["Key"], Loc["Key"], _loc["Key"], _loc?["Key"]
$appPattern = '(?<![\w])(?:@?Loc|_loc)\??\["([^"]+)"\]'
$appResult = Process-LocalizationResource -ResourceBaseName "AppResources" -Pattern $appPattern

# 2. Process Business Edition UI (BusinessResources.resx)
# Pattern matches @BizLoc["Key"], BizLoc["Key"], _bizLoc["Key"], _bizLoc?["Key"]
# Ignore keys that already exist in AppResources (common actions like Save, Cancel are inherited via CompositeBusinessLocalizer)
$bizPattern = '(?<![\w])(?:@?BizLoc|_bizLoc)\??\["([^"]+)"\]'
$bizResult = Process-LocalizationResource -ResourceBaseName "BusinessResources" -Pattern $bizPattern -SharedKeysToIgnore $appResult.ExistingKeys

Write-Host "`n=== Extraction Complete ===" -ForegroundColor Cyan
Write-Host "Family Edition (AppResources): $($appResult.FoundCount) active keys (+$($appResult.NewCount), -$($appResult.PrunedCount))" -ForegroundColor Gray
Write-Host "Business Edition (BusinessResources): $($bizResult.FoundCount) active keys (+$($bizResult.NewCount), -$($bizResult.PrunedCount))" -ForegroundColor Gray
