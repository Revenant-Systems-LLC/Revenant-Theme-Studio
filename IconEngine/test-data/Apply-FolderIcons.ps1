<# 
Apply-FolderIcons.ps1

Matches .ico files to folders by name and applies them via desktop.ini.

Example:
  .\Apply-FolderIcons.ps1 -IconDir "D:\icons" -TargetRoot "D:\Games" -Recurse -Verbose
#>

[CmdletBinding(SupportsShouldProcess=$true)]
param(
  [Parameter(Mandatory)]
  [ValidateScript({ Test-Path $_ -PathType Container })]
  [string]$IconDir,

  [Parameter(Mandatory)]
  [ValidateScript({ Test-Path $_ -PathType Container })]
  [string]$TargetRoot,

  # Search folders under TargetRoot recursively (recommended)
  [switch]$Recurse,

  # If set, also tries a "soft match" (e.g., icon "adobe-photoshop.ico" matches folder "Adobe Photoshop")
  [switch]$FuzzyMatch,

  # Don’t touch anything; just print what would happen
  [switch]$DryRun
)

function Normalize-Name {
  param([string]$s)
  # Lowercase, remove common separators, collapse whitespace
  ($s.ToLowerInvariant() -replace '[\s\-_\.]+','' -replace '[\(\)\[\]\{\}]','').Trim()
}

function Write-FolderIcon {
  param(
    [string]$FolderPath,
    [string]$IconPath
  )

  $desktopIni = Join-Path $FolderPath 'desktop.ini'

  # Use an ABSOLUTE icon path. IconResource wants "path,0"
  $absIcon = (Resolve-Path $IconPath).Path

  $content = @"
[.ShellClassInfo]
IconResource=$absIcon,0
"@

  if ($DryRun) {
    Write-Host "[DRYRUN] Would write $desktopIni -> $absIcon"
    return
  }

  # Ensure file is created as Unicode (UTF-16 LE) which Windows is happiest with for ini files
  Set-Content -LiteralPath $desktopIni -Value $content -Encoding Unicode -Force

  # Set desktop.ini attributes: Hidden + System
  attrib +h +s "$desktopIni" | Out-Null

  # Set folder attributes: System (and ReadOnly helps Explorer treat it as customizable)
  attrib +s +r "$FolderPath" | Out-Null
}

# --- Collect icons ---
$icons = Get-ChildItem -LiteralPath $IconDir -Filter '*.ico' -File
if (-not $icons) { throw "No .ico files found in: $IconDir" }

# --- Collect folders ---
$folderQuery = @{
  LiteralPath = $TargetRoot
  Directory   = $true
}
$folders = if ($Recurse) {
  Get-ChildItem -LiteralPath $TargetRoot -Directory -Recurse -Force
} else {
  Get-ChildItem -LiteralPath $TargetRoot -Directory -Force
}

# Build fast lookup: exact folder name -> list of folders
$byExact = @{}
foreach ($f in $folders) {
  $name = $f.Name
  if (-not $byExact.ContainsKey($name)) { $byExact[$name] = @() }
  $byExact[$name] += $f.FullName
}

# Build fuzzy lookup if requested
$byNorm = @{}
if ($FuzzyMatch) {
  foreach ($f in $folders) {
    $k = Normalize-Name $f.Name
    if (-not $byNorm.ContainsKey($k)) { $byNorm[$k] = @() }
    $byNorm[$k] += $f.FullName
  }
}

$applied = 0
$skipped = 0

foreach ($ico in $icons) {
  $base = [IO.Path]::GetFileNameWithoutExtension($ico.Name)

  $targets = @()

  # Exact match first
  if ($byExact.ContainsKey($base)) {
    $targets = $byExact[$base]
  }
  elseif ($FuzzyMatch) {
    $k = Normalize-Name $base
    if ($byNorm.ContainsKey($k)) {
      $targets = $byNorm[$k]
    }
  }

  if (-not $targets -or $targets.Count -eq 0) {
    Write-Verbose "No folder match for icon: $($ico.Name)"
    $skipped++
    continue
  }

  foreach ($t in $targets) {
    $msg = "Apply icon '$($ico.FullName)' to folder '$t'"
    if ($PSCmdlet.ShouldProcess($t, $msg)) {
      try {
        Write-FolderIcon -FolderPath $t -IconPath $ico.FullName
        Write-Host "OK  -> $t  <=  $($ico.Name)"
        $applied++
      } catch {
        Write-Warning "FAILED -> $t : $($_.Exception.Message)"
      }
    }
  }
}

Write-Host ""
Write-Host "DONE: Applied=$applied  Skipped(no match)=$skipped"
Write-Host "If icons don't refresh, restart Explorer:  taskkill /f /im explorer.exe && start explorer.exe"