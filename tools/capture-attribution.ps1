# Runs the plugin against a *copy of the real data directory* and writes the frames
# for the two groups that are not accounts: 未归属 and 归档.
#
#   pwsh -File tools\capture-attribution.ps1
#
# Why a script of its own rather than four more entries in capture-redesign.ps1:
# that run photographs the plugin-format fixture, whose events are mock data with
# every account present in its own csettings. The two groups only exist when the log
# and the account list *disagree* — a client that wrote no account id, and an id the
# list no longer names — and the fixture has neither, because a fixture that
# disagreed with itself would be a broken fixture. These frames therefore need the
# real log, and the real log is read from a copy: --data-dir is the flag the host
# passes, and nothing here writes to %LOCALAPPDATA%\BalancePet.
#
# The source is dist\_perf-data (the working copy of the user's own data directory,
# 1,396 lines and 4 monitors). It is copied — never used in place — because the
# plugin records observed balances and the archive ceiling into whatever directory it
# is given, and a run that wrote those into the source would leave the next run
# reading a state this one produced.
#
# The screenshots go outside this repository, so nothing here can be committed by
# accident.

[CmdletBinding()]
param(
    [string]$Output = "",
    [string]$Source = ""
)

$ErrorActionPreference = "Stop"
$repository = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
# repository -> dist\_ext-repos -> dist -> the workspace root
$workspace = [System.IO.Path]::GetFullPath((Join-Path $repository "..\..\.."))
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $workspace "dist\usage-redesign-shots" }
$shots = [System.IO.Path]::GetFullPath($Output)
if ([string]::IsNullOrWhiteSpace($Source)) { $Source = Join-Path $workspace "dist\_perf-data" }

if (-not (Test-Path (Join-Path $Source "usage-events.ndjson"))) {
    throw "No real log at $Source. This run needs the copy of the data directory, not the plugin fixture."
}
if (-not (Test-Path (Join-Path $Source "csharp-settings.json"))) {
    throw "No account list at ${Source}: without it every record is archived and the frames show one group twice."
}

New-Item -ItemType Directory -Force -Path $shots | Out-Null

$project = Join-Path $repository "src\BalancePet.UsageAnalytics.csproj"
$exe = Join-Path $repository "src\bin\Release\net8.0-windows\BalancePet.UsageAnalytics.exe"
dotnet build $project -c Release --nologo -v:q | Out-Null
if (-not (Test-Path $exe)) { throw "No executable at $exe" }

# One directory per *ceiling*, rebuilt from the source on every run, for the same
# reason the recharge frames in capture-redesign.ps1 get one each: the archive's
# ceiling is written into the plugin's own file inside --data-dir, so frame 33 (the
# ceiling moved by clicks) and frame 31 (the default) cannot share a directory
# without one of them being photographed against the other's state.
$variantRoot = Join-Path $workspace "dist\ui-variants"
$dataDirs = @{
    Default = Join-Path $variantRoot "attribution-real"
    Raised  = Join-Path $variantRoot "attribution-real-raised"
}
foreach ($directory in $dataDirs.Values) {
    if (Test-Path $directory) { Remove-Item $directory -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    Copy-Item (Join-Path $Source "*") $directory -Force
    # A ceiling this run did not set is a ceiling the previous run left: the frames
    # below claim what 200 and 500 look like, and a stale file would silently make
    # the claim about a number nobody chose.
    $state = Join-Path $directory "usage-analytics-archive.v1.json"
    if (Test-Path $state) { Remove-Item $state -Force }
}

# 30  the 未归属 group entered the way a person enters it: the account column's own
#     row is clicked (--select-account), not the scope set before the window is built.
#     The frame has to carry three things at once — the group's own row and its count
#     in the account column, the badge over the list, and the advice sentence that says
#     what to do about the records — so it is taken with the group scoped, where all
#     three are on screen together.
# 31  the 归档 group, same click. The account column's row states 「账户删除，记录归档」,
#     the strip names the removed ids and the ceiling control beside it writes it, and
#     the badge over the list repeats the row's own words.
# 32  the census with nothing scoped, which is the one frame where every group's count
#     and the total are legible in the same picture: the account column lists all three
#     groups' rows and the line under the ledger adds them up.
# 33  the ceiling moved by two clicks on the control (200 → 500), in a directory of its
#     own. The clicks are the gesture, so the shot is of a state a click produced, and
#     the run prints the file's new contents so "it persisted" is a reading rather than
#     a claim. This frame's ceiling is left on disk on purpose: the run after it starts
#     from a rebuilt directory, and the reader can open the file to see it.
$frames = @(
    @{ Name = "30-unattributed-group.png";  Dir = $dataDirs.Default; Args = @("--page", "usage", "--select-account", "__unattributed__", "--capture", (Join-Path $shots "30-unattributed-group.png")) },
    @{ Name = "31-archive-group.png";       Dir = $dataDirs.Default; Args = @("--page", "usage", "--select-account", "__archived__", "--capture", (Join-Path $shots "31-archive-group.png")) },
    @{ Name = "32-count-census.png";        Dir = $dataDirs.Default; Args = @("--page", "usage", "--capture", (Join-Path $shots "32-count-census.png")) },
    @{ Name = "33-archive-ceiling-500.png"; Dir = $dataDirs.Raised;  Args = @("--page", "usage", "--select-account", "__archived__", "--archive-limit-step", "2", "--capture", (Join-Path $shots "33-archive-ceiling-500.png")) }
)

foreach ($frame in $frames) {
    $png = Join-Path $shots $frame.Name
    if (Test-Path $png) { Remove-Item $png -Force }
    $arguments = @("--data-dir", $frame.Dir) + $frame.Args
    Write-Host ("--- {0}: {1}" -f $frame.Name, ($arguments -join " "))
    & $exe @arguments
    # The encoder closes its stream as the process exits, so the file is given a
    # moment rather than declared missing on the first look.
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $png) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 150 }
    if (-not (Test-Path $png)) { throw "$($frame.Name) was not written" }
    Write-Host ("    {0} bytes" -f (Get-Item $png).Length)
}

# The ceiling frame's own evidence: the file the control wrote, and the ceiling it
# now holds. Printed rather than asserted, because the claim is about this run.
$raised = Join-Path $dataDirs.Raised "usage-analytics-archive.v1.json"
Write-Host ""
Write-Host "archive ceiling file (frame 33):"
if (Test-Path $raised) { Write-Host ((Get-Content $raised -Raw).Trim()) } else { Write-Host "  (absent)" }

Write-Host ""
Write-Host "screenshots:"
foreach ($frame in $frames) { Write-Host ("  {0}" -f (Join-Path $shots $frame.Name)) }
