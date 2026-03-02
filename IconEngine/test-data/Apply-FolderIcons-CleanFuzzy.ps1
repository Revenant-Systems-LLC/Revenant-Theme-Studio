<# 
Apply-FolderIcons-CleanFuzzy.ps1

Behavior:
- Exact folder name match wins.
- Else "normalized" match (ignores spaces/dashes/underscores/case).
- Fuzzy matches are applied ONLY when they are unambiguous (one folder).
- Skips Windows/Program Files/etc for safety.
- Writes desktop.ini + required attributes.

Run DryRun first.
#>

[CmdletBinding(SupportsShouldProcess=$true)]
param(
  [Parameter(Mandatory)]
  [ValidateScript({ Test-Path $_ -PathType Container })]
  [string]$IconDir,

  # One or more roots to scan (recommended). 
  # If you truly insist on all of C:, use @('C:\') but it will be slow.
  [Parameter(Mandatory)]
  [string[]]$TargetRoots,

  [switch]$Recurse = $true,

  # Show what would happen
  [switch]$DryRun,

  # Optional: write a CSV log for review
  [string]$LogCsv = "$PWD\icon-map-log.csv"
)

function Normalize-Name([string]$s) {
  ($s.ToLowerInvariant() `
    -replace '[\s\-_\.]+','' `
    -replace '[\(\)\[\]\{\}]','' `
  ).Trim()
}

function Is-ExcludedPath([string]$fullPath) {
  $p = $fullPath.ToLowerInvariant()
  $excluded = @(
    'c:\windows\',
    'c:\program files\',
    'c:\program files (x86)\',
    'c:\programdata\',
    'c:\$recycle.bin\',
    'c:\system volume information\'
  )
  foreach ($x in $excluded) { if ($p.StartsWith($x)) { return $true } }
  return $false
}

function Write-FolderIcon([string]$FolderPath, [string]$IconPath) {
  $desktopIni = Join-Path $FolderPath 'desktop.ini'
  $absIcon = (Resolve-Path $IconPath).Path

  $content = @"
[.ShellClassInfo]
IconResource=$absIcon,0
"@

  if ($DryRun) { return }

  Set-Content -LiteralPath $desktopIni -Value $content -Encoding Unicode -Force
  attrib +h +s "$desktopIni" | Out-Null
  attrib +s +r "$FolderPath" | Out-Null
}

# --- Collect icons ---
$icons = Get-ChildItem -LiteralPath $IconDir -Filter '*.ico' -File
if (-not $icons) { throw "No .ico files found in: $IconDir" }

# --- Collect folders ---
$folders = New-Object System.Collections.Generic.List[System.IO.DirectoryInfo]
foreach ($root in $TargetRoots) {
  if (-not (Test-Path $root -PathType Container)) {
    Write-Warning "Skipping missing root: $root"
    continue
  }

  $items = if ($Recurse) {
    Get-ChildItem -LiteralPath $root -Directory -Recurse -Force -Attributes !ReparsePoint -ErrorAction SilentlyContinue
  } else {
    Get-ChildItem -LiteralPath $root -Directory -Force -Attributes !ReparsePoint -ErrorAction SilentlyContinue
  }

  foreach ($f in $items) {
    if (-not (Is-ExcludedPath $f.FullName)) { $folders.Add($f) }
  }
}

# Lookup tables
$byExact = @{}   # folderName -> [paths...]
$byNorm  = @{}   # normalized(folderName) -> [paths...]

foreach ($f in $folders) {
  $name = $f.Name
  if (-not $byExact.ContainsKey($name)) { $byExact[$name] = @() }
  $byExact[$name] += $f.FullName

  $k = Normalize-Name $name
  if (-not $byNorm.ContainsKey($k)) { $byNorm[$k] = @() }
  $byNorm[$k] += $f.FullName
}

# Logging
$log = New-Object System.Collections.Generic.List[object]

$applied = 0
$skippedNoMatch = 0
$skippedAmbiguous = 0

foreach ($ico in $icons) {
  $base = [IO.Path]::GetFileNameWithoutExtension($ico.Name)

  $matchType = $null
  $targets = @()

  # 1) Exact match
  if ($byExact.ContainsKey($base)) {
    $targets = $byExact[$base]
    $matchType = "exact"
  }
  else {
    # 2) Normalized match (clean fuzzy)
    $k = Normalize-Name $base
    if ($byNorm.ContainsKey($k)) {
      $targets = $byNorm[$k]
      $matchType = "normalized"
    }
  }

  if (-not $targets -or $targets.Count -eq 0) {
    $skippedNoMatch++
    $log.Add([pscustomobject]@{
      Icon      = $ico.Name
      MatchType = "none"
      Folder    = ""
      Action    = "skipped_no_match"
    })
    continue
  }

  # Cleanliness rule: if fuzzy/normalized match returns multiple folders, skip it.
  if ($matchType -ne "exact" -and $targets.Count -ne 1) {
    $skippedAmbiguous++
    $log.Add([pscustomobject]@{
      Icon      = $ico.Name
      MatchType = $matchType
      Folder    = ($targets -join " | ")
      Action    = "skipped_ambiguous"
    })
    continue
  }

  foreach ($t in $targets) {
    $msg = "Apply icon '$($ico.Name)' ($matchType) to '$t'"
    if ($DryRun -or $PSCmdlet.ShouldProcess($t, $msg)) {
      try {
        if ($DryRun) {
          Write-Host "[DRYRUN][$matchType] $t  <=  $($ico.Name)"
        } else {
          Write-FolderIcon -FolderPath $t -IconPath $ico.FullName
          Write-Host "OK  [$matchType] $t  <=  $($ico.Name)"
        }

        $applied++
        $log.Add([pscustomobject]@{
          Icon      = $ico.Name
          MatchType = $matchType
          Folder    = $t
          Action    = "applied"
        })
      } catch {
        Write-Warning "FAILED -> $t : $($_.Exception.Message)"
        $log.Add([pscustomobject]@{
          Icon      = $ico.Name
          MatchType = $matchType
          Folder    = $t
          Action    = "failed: $($_.Exception.Message)"
        })
      }
    }
  }
}

# Write log
try {
  $log | Export-Csv -NoTypeInformation -Encoding UTF8 -Force -Path $LogCsv
  Write-Host "LOG -> $LogCsv"
} catch {
  Write-Warning "Could not write log CSV: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "DONE: Applied=$applied  Skipped(no match)=$skippedNoMatch  Skipped(ambiguous fuzzy)=$skippedAmbiguous"
Write-Host "Refresh Explorer: taskkill /f /im explorer.exe && start explorer.exe"