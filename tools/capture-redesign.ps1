# Runs the plugin against the plugin-format fixture and writes the redesign
# screenshots. Nothing here is packaged and nothing here ships: it is the run that
# turns "it builds" into "here is the window".
#
#   pwsh -File tools\capture-redesign.ps1
#
# The fixture is produced by experiments/usage-ui-mockup/plugin-fixture-tool/, which
# converts the approved prototype's mock data into the three documents this
# plugin's own readers accept. --data-dir is the flag the host passes, so the run
# uses the production code path and no test hook: the only development-only flag is
# --capture, which asks for one named frame and exits.
#
# The screenshots go outside this repository, so nothing here can be committed by
# accident.

[CmdletBinding()]
param(
    [string]$Output = "",
    [double]$ExpandAt = 640,
    [double]$ReadingAt = 150
)

$ErrorActionPreference = "Stop"
$repository = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
# repository -> dist\_ext-repos -> dist -> the workspace root
$workspace = [System.IO.Path]::GetFullPath((Join-Path $repository "..\..\.."))
$fixture = Join-Path $workspace "experiments\usage-ui-mockup\plugin-fixture"
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $workspace "dist\usage-redesign-shots" }
$shots = [System.IO.Path]::GetFullPath($Output)

if (-not (Test-Path (Join-Path $fixture "usage-events.ndjson"))) {
    throw "No fixture at $fixture. Run: dotnet run --project experiments\usage-ui-mockup\plugin-fixture-tool\MakePluginFixture.csproj -c Release"
}

# The fixture is anchored to the day it was generated, so its "today" is that day.
# Photographed a day later it shows an empty today column, and a dashboard whose
# clock disagrees with its data reads as a broken window rather than as a stale
# fixture. Regenerate instead of photographing the mismatch.
$balanceDocument = Get-Content (Join-Path $fixture "balance-usage.v1.json") -Raw | ConvertFrom-Json
if ([datetime]$balanceDocument.updated_at -lt (Get-Date).Date) {
    Write-Warning ("fixture written {0}; today's readings will be empty. Regenerate: dotnet run --project experiments\usage-ui-mockup\plugin-fixture-tool\MakePluginFixture.csproj -c Release" -f $balanceDocument.updated_at)
}

New-Item -ItemType Directory -Force -Path $shots | Out-Null
$project = Join-Path $repository "src\BalancePet.UsageAnalytics.csproj"
$exe = Join-Path $repository "src\bin\Release\net8.0-windows\BalancePet.UsageAnalytics.exe"

dotnet build $project -c Release --nologo -v:q | Out-Null
if (-not (Test-Path $exe)) { throw "No executable at $exe" }

# One frame per run. The expanded row is index 6 of the ledger as the fixture
# generates it: it carries five sub-requests and, at five rows down the list, it
# opens entirely inside the visible area — a row opened below the fold is a shot of
# the top of the list, which proves the animation ran and shows nothing of it.
#
# -ExpandAt names the millisecond of the opening to hold, and the default is the
# frame the panel is *full* at rather than a frame in the middle of it: the five
# sub-requests fade in over ten staggered lines, and a frame taken while the last of
# them is still at zero opacity is a photograph of a container holding rows that
# have not arrived — which is indistinguishable, in one still, from a container that
# is too tall for what it holds. The claim "the detail panel has no empty band at the
# bottom" can only be photographed once the panel is complete; the mid-flight frame
# is still available with -ExpandAt 240.
#
# 05 is the reading motion: the balance and today's cost rolling in from FlowCost
# when a watched file changes. It is held 150 ms into a 420 ms rise and a 900 ms
# decay, so the figure is amber and off its baseline in the same frame — the log
# prints both numbers.
$frames = @(
    @{ Name = "01-dashboard.png"; Args = @("--capture", (Join-Path $shots "01-dashboard.png")) },
    @{ Name = "02-usage-ledger.png"; Args = @("--page", "usage", "--capture", (Join-Path $shots "02-usage-ledger.png")) },
    @{ Name = "03-row-expanded.png"; Args = @("--page", "usage", "--expand", "6", "--at", $ExpandAt.ToString([cultureinfo]::InvariantCulture), "--capture", (Join-Path $shots "03-row-expanded.png")) },
    @{ Name = "04-api-balance-bar.png"; Args = @("--capture", (Join-Path $shots "04-api-balance-bar.png"), "--account", "acc_4d18") },
    @{ Name = "05-flowcost-decay.png"; Args = @("--capture", (Join-Path $shots "05-flowcost-decay.png"), "--reading-motion", $ReadingAt.ToString([cultureinfo]::InvariantCulture)) }
)

# The five frames below are the ones a change to the list, the filter bar or the
# subscription card has to be photographed through, because each of them is a
# *click* rather than a state the window can be started in:
#
#   10 clicks an account in the list. -Account would set the same scope before the
#      window is built, which is exactly the path that cannot show whether the
#      list's own highlight follows the account: the highlight was painted once, on
#      the first refresh, and no later scope change moved it.
#   11 clicks a client chip, so the chip that is drawn raised is the one that was
#      clicked rather than the one the window opened on.
#   12/13/14 are the subscription quota card: closed (its resting state), open, and
#      held 125 ms into the 250 ms fold. The mid-flight frame is the one that shows
#      the two extra windows arriving rather than simply being there — the log
#      prints the height it was held at.
#
# Numbered from 10 because 06-09 were taken by an earlier round's frames, which were
# documented nowhere. Those four were deleted in the fourth round (REDESIGN-NOTES §4.6)
# and the numbers were NOT re-packed: a frame's number is how the notes and the handoff
# refer to it, and shifting 10-18 down by four would invalidate every reference to them
# in order to close a gap nobody reads.
$clickFrames = @(
    @{ Name = "10-account-selected.png"; Args = @("--select-account", "acc_4d18", "--capture", (Join-Path $shots "10-account-selected.png")) },
    @{ Name = "11-filter-chips.png"; Args = @("--select-provider", "anthropic", "--page", "usage", "--capture", (Join-Path $shots "11-filter-chips.png")) },
    @{ Name = "12-quota-fold-closed.png"; Args = @("--account", "acc_7f3a", "--capture", (Join-Path $shots "12-quota-fold-closed.png")) },
    @{ Name = "13-quota-fold-open.png"; Args = @("--account", "acc_7f3a", "--expand-quota", "--capture", (Join-Path $shots "13-quota-fold-open.png")) },
    @{ Name = "14-quota-fold-mid.png"; Args = @("--account", "acc_7f3a", "--expand-quota", "--quota-at", "125", "--capture", (Join-Path $shots "14-quota-fold-mid.png")) }
)
$frames += $clickFrames

# 15 is the reading's provenance: the account row in the 250 px list and the card's
# primary bar are one resolution of one account's settings, so the two name the same
# window and state the same percentage and the same used / ceiling pair. It needs no
# click — the run is scoped to the account with -Account, which is the flag the other
# quota frames use — and the run prints both texts and compares them
# (capture: quota account=… row="…" card="…" same=True), because "the two agree" is a
# claim a reader should not have to check by eye across 700 px of window.
$sourceFrames = @(
    @{ Name = "15-account-row-matches-quota-card.png"; Args = @("--account", "acc_7f3a", "--capture", (Join-Path $shots "15-account-row-matches-quota-card.png")) }
)
$frames += $sourceFrames

# --- the pay-as-you-go recharge gauge: 16, 17, 18 ------------------------------
#
# The gauge's denominator is the balance the last recharge left behind, and the only
# record a recharge can be read out of is this plugin's own observation file: the host's
# ledger counts a balance going down and drops it going up, so a series built from that
# document can never show money arriving. Three frames therefore need three *histories*
# rather than three windows, and each gets its own data directory under
# dist\ui-variants\ — the production code path, one directory over, holding the fixture
# plus the plugin state file that frame is about. They are rebuilt from the fixture on
# every run, so a frame cannot be photographed against the state a previous run left.
#
# The state file is the plugin's own (usage-analytics-balances.v1.json, written into
# whatever --data-dir it is given). Its numbers here are the fixture's own balance for
# acc_4d18 (4.13): a top-up to 12.40 that has been spent since, a top-up to 3.20 with
# nothing left of it to spend, and — the third — one move short of the current balance,
# so the run itself has to notice the recharge.
$variantRoot = Join-Path $workspace "dist\ui-variants"
$rechargeDirs = @{
    Recorded = Join-Path $variantRoot "recharge-recorded"
    None     = Join-Path $variantRoot "recharge-none"
    Detected = Join-Path $variantRoot "recharge-detected"
}
$rechargeStates = @{
    Recorded = @'
{
  "schema": "balancepet.usage-analytics.balances.v1",
  "accounts": [
    {
      "account_id": "acc_4d18",
      "currency": "USD",
      "baseline": 12.40,
      "baseline_at": "2026-10-11T20:40:00+08:00",
      "observations": [
        { "at": "2026-10-11T20:40:00+08:00", "balance": 12.40 }
      ]
    }
  ]
}
'@
    None     = ""
    Detected = @'
{
  "schema": "balancepet.usage-analytics.balances.v1",
  "accounts": [
    {
      "account_id": "acc_4d18",
      "currency": "USD",
      "baseline": 3.20,
      "baseline_at": "2026-10-11T19:10:00+08:00",
      "observations": [
        { "at": "2026-10-11T19:10:00+08:00", "balance": 3.20 }
      ]
    }
  ]
}
'@
}
foreach ($name in $rechargeDirs.Keys) {
    $directory = $rechargeDirs[$name]
    if (Test-Path $directory) { Remove-Item $directory -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    Copy-Item (Join-Path $fixture "*") $directory -Force
    if ($rechargeStates[$name].Length -gt 0) {
        Set-Content -Path (Join-Path $directory "usage-analytics-balances.v1.json") -Value $rechargeStates[$name] -Encoding utf8
    }
}

$rechargeFrames = @(
    @{ Name = "16-recharge-gauge.png"; DataDir = $rechargeDirs.Recorded; Args = @("--account", "acc_4d18", "--capture", (Join-Path $shots "16-recharge-gauge.png")) },
    @{ Name = "17-recharge-unrecorded.png"; DataDir = $rechargeDirs.None; Args = @("--account", "acc_4d18", "--capture", (Join-Path $shots "17-recharge-unrecorded.png")) },
    @{ Name = "18-recharge-detected.png"; DataDir = $rechargeDirs.Detected; Args = @("--account", "acc_4d18", "--capture", (Join-Path $shots "18-recharge-detected.png")) }
)
$frames += $rechargeFrames

# 19 is the compatibility clause of the reading rule: settings written before windows
# existed carry one monthly ceiling and the day it renews, and those settings still have
# to show a reading. Same account, same single window, same numbers as 15 — only the
# field the ceiling is written in differs — so this is what "旧设置仍要能显示" looks like,
# and the run prints the same row-equals-card line for it.
$legacyDirectory = Join-Path $variantRoot "legacy-quota-limit"
if (Test-Path $legacyDirectory) { Remove-Item $legacyDirectory -Recurse -Force }
New-Item -ItemType Directory -Force -Path $legacyDirectory | Out-Null
Copy-Item (Join-Path $fixture "*") $legacyDirectory -Force
Set-Content -Path (Join-Path $legacyDirectory "csharp-settings.json") -Encoding utf8 -Value @'
{
  "monitors": [
    {
      "id": "acc_7f3a",
      "name": "DeepSeek Harness",
      "endpoint": "https://api.deepseek.com/user/balance",
      "preset_id": "deepseek-platform",
      "is_subscription": true,
      "quota_limit": 120,
      "quota_reset_day": 11
    },
    {
      "id": "acc_4d18",
      "name": "Relay · Primary",
      "endpoint": "https://relay.example.invalid/v1/usage",
      "preset_id": "",
      "is_subscription": false,
      "warning_below": 7
    }
  ]
}
'@

$frames += @(
    @{ Name = "19-legacy-quota-limit.png"; DataDir = $legacyDirectory; Args = @("--account", "acc_7f3a", "--capture", (Join-Path $shots "19-legacy-quota-limit.png")) }
)

# The plugin records the balances it observes in a file of its own inside whatever
# --data-dir it is given, so a capture run leaves one behind in the fixture directory.
# The fixture directory belongs to the generator, and a state file left there would let
# a later frame be photographed against a history an earlier run recorded. One capture,
# one fixture: clear it before the run and afterwards.
$pluginState = Join-Path $fixture "usage-analytics-balances.v1.json"
if (Test-Path $pluginState) { Remove-Item $pluginState -Force }

foreach ($frame in $frames) {
    $png = Join-Path $shots $frame.Name
    if (Test-Path $png) { Remove-Item $png -Force }
    $dataDir = if ($frame.ContainsKey("DataDir")) { $frame.DataDir } else { $fixture }
    $arguments = @("--data-dir", $dataDir) + $frame.Args
    Write-Host ("--- {0}: {1}" -f $frame.Name, ($arguments -join " "))
    & $exe @arguments
    # The encoder closes its stream as the process exits, so the file is given a
    # moment rather than declared missing on the first look.
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $png) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 150 }
    if (-not (Test-Path $png)) { throw "$($frame.Name) was not written" }
    Write-Host ("    {0} bytes" -f (Get-Item $png).Length)
}

if (Test-Path $pluginState) { Remove-Item $pluginState -Force }

Write-Host ""
Write-Host "screenshots:"
Get-ChildItem $shots -Filter *.png | ForEach-Object { Write-Host ("  {0}  {1} bytes" -f $_.FullName, $_.Length) }
