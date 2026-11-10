# Theme Studio sandbox run.
# Runs at sandbox logon:
#   1. writes an environment report
#   2. runs the test harness, with a hard time limit
#   3. shuts the sandbox down
# so a run is unattended start to finish. Reports land in C:\rts\results, which is
# mapped to the host's tests\sandbox\results. No step plays sound.

$ErrorActionPreference = 'Continue'
$stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
$results = 'C:\rts\results'
$envRep  = Join-Path $results "env-$stamp.txt"

function Line($text) { Add-Content -Path $envRep -Value $text -Encoding UTF8 }

$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
Line ("Windows build : {0}.{1} ({2})" -f $cv.CurrentBuild, $cv.UBR, $cv.DisplayVersion)
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
Line ("User          : {0}, elevated admin: {1}" -f [Environment]::UserName,
      (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))

# The kit folder is read-only. Copy the harness to local disk so it runs from a normal path.
$local = 'C:\rts-local'
Copy-Item -Path 'C:\rts\kit\tests' -Destination $local -Recurse -Force
Line ("Kit copied    : {0} files" -f (Get-ChildItem $local -Recurse -File | Measure-Object).Count)

$icons = 'C:\rts-icons'
if (Test-Path 'C:\rts\kit\icons') { Copy-Item -Path 'C:\rts\kit\icons' -Destination $icons -Recurse -Force }
Line ("Icons copied  : {0} files" -f (Get-ChildItem $icons -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count)

# Which tests run: kit\select.txt holds a list such as "1,4" or "1,5"; the harness default is "1,4".
$select = '1,4'
if (Test-Path 'C:\rts\kit\select.txt') { $select = (Get-Content 'C:\rts\kit\select.txt' -Raw).Trim() }
Line ("Tests         : {0}" -f $select)

$exe = Join-Path $local 'RtsSandboxTests.exe'
$p = Start-Process -FilePath $exe -ArgumentList @('run', $results, $icons, $select) -PassThru -WindowStyle Hidden `
       -RedirectStandardError (Join-Path $results "stderr-$stamp.txt")
$null = $p.Handle   # PowerShell only records ExitCode if the handle is touched before the process exits
if ($p.WaitForExit(10 * 60 * 1000)) {
    Line ("Harness exit  : {0}" -f $p.ExitCode)
} else {
    Line "Harness exit  : TIMEOUT after 10 minutes, killed"
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
Line "DONE"

Stop-Computer -Force
