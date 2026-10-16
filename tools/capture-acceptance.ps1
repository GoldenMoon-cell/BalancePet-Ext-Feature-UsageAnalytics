# The acceptance frames for the responsive-layout / no-combined-account / per-account
# currency / hover-bubble round.
#
#   pwsh -File tools\capture-acceptance.ps1
#
# Every frame is one run of the production executable against a copy of the captured
# dataset, one --capture away from an ordinary launch. The data directory is a copy under
# dist\ because the plugin writes its own balance record into whatever --data-dir it is
# given, and the captured dataset is the one thing in this workspace that must not move
# under a later comparison.
#
# What each frame is for:
#
#   40-43  one task list per window width. The acceptance for the resize report is four
#          numbers per width — the content area, the table, whether a horizontal bar is
#          drawn and how much of the window the table leaves as dead space — and the run
#          prints all four (capture: ledger …) so the numbers in the notes are read off
#          the run rather than measured in a PNG.
#   44-45  the four readouts: two by two in a narrow window, four across in a wide one.
#          --width is a real size, not a scaled render; the layout is a function of it.
#   46     the 未归属 group, which is the other kind of scope the rail still has.
#   47     the dashboard at 1250, which is where the rail, the account table's 币种 column
#          and the hero card are all in one frame.
#   48-51  the hover pair, before and after. A hover cannot be synthesized — the pointer
#          belongs to the desk — so the bubble is opened through the element's own ToolTip
#          (--tip N) and composited onto the window's pixels, because a tooltip lives in a
#          popup window of its own. 48/49 are a row whose model WAS reported (the bubble
#          was white with near-white text in it); 50/51 are a row whose model was NOT
#          (the bubble was empty, which is the dotted white rectangle in the report).
#          The "before" frames come from tools\capture-tooltip-before.ps1, which builds the
#          same source with only the two tooltip fixes reverted.

[CmdletBinding()]
param(
    [string]$Output = "",
    [string]$DataDirectory = ""
)

$ErrorActionPreference = "Stop"
$repository = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
# repository -> dist\_ext-repos -> dist -> the workspace root
$workspace = [System.IO.Path]::GetFullPath((Join-Path $repository "..\..\.."))
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $workspace "dist\usage-redesign-shots" }
$shots = [System.IO.Path]::GetFullPath($Output)

$data = if ([string]::IsNullOrWhiteSpace($DataDirectory)) { Join-Path $workspace "dist\shots-data" } else { [System.IO.Path]::GetFullPath($DataDirectory) }
$captured = Join-Path $workspace "dist\_perf-data"
if (-not (Test-Path (Join-Path $data "usage-events.ndjson"))) {
    if (-not (Test-Path (Join-Path $captured "usage-events.ndjson"))) {
        throw "No dataset at $captured. A capture needs real events, not a fixture."
    }
    New-Item -ItemType Directory -Force -Path $data | Out-Null
    Copy-Item (Join-Path $captured "*") $data -Force
}

New-Item -ItemType Directory -Force -Path $shots | Out-Null
$project = Join-Path $repository "src\BalancePet.UsageAnalytics.csproj"
$exe = Join-Path $repository "src\bin\Release\net8.0-windows\BalancePet.UsageAnalytics.exe"
dotnet build $project -c Release --nologo -v:q | Out-Null
if (-not (Test-Path $exe)) { throw "No executable at $exe" }

# The variant the empty-tooltip frame needs: the captured dataset plus one task whose
# single sub-request reported no model, which is the row the user hovered. No dataset on
# this machine contains one, so one is appended rather than the frame being dropped. See
# tools\capture-tooltip-before.ps1, which builds the same directory for its half of the
# pair and is the authority on that file's shape.
$variant = Join-Path $workspace "dist\ui-variants\empty-model-detail"
if (-not (Test-Path (Join-Path $variant "usage-events.ndjson"))) {
    New-Item -ItemType Directory -Force -Path $variant | Out-Null
    Copy-Item (Join-Path $data "*") $variant -Force
    $line = '{"schema":"balancepet.usage.v1","event_id":"evt-empty-model-detail-0001",'
        + '"occurred_at":"2026-10-12T12:27:55.0000000+08:00","kind":"llm_request","provider":"Codex",'
        + '"account_id":"","model":"gpt-6-astra","success":true,"input_tokens":120,"output_tokens":40,'
        + '"currency":"","duration_ms":900,"reasoning_effort":"low","details":[{"occurred_at":'
        + '"2026-10-12T12:27:54.5000000+08:00","model":"","reasoning_effort":"","input_tokens":120,'
        + '"output_tokens":40}]}'
    Add-Content -Path (Join-Path $variant "usage-events.ndjson") -Value $line -Encoding utf8
}

# The scope the ledger frames use: an account whose records are billed in CNY, which is
# the case a hard-coded "$" and a cross-account total are both wrong about. 「default」 is
# the DeepSeek Harness account in the host's own settings file.
$frames = @(
    @{ Name = "40-ledger-980.png"; Args = @("--page", "usage", "--account", "default", "--width", "980", "--height", "900") },
    @{ Name = "41-ledger-1250.png"; Args = @("--page", "usage", "--account", "default", "--width", "1250", "--height", "900") },
    @{ Name = "42-ledger-1400.png"; Args = @("--page", "usage", "--account", "default", "--width", "1400", "--height", "900") },
    @{ Name = "43-ledger-1918.png"; Args = @("--page", "usage", "--account", "default", "--width", "1918", "--height", "960") },
    @{ Name = "44-dashboard-980-two-by-two.png"; Args = @("--account", "default", "--width", "980", "--height", "1000") },
    @{ Name = "45-dashboard-1400-four-across.png"; Args = @("--account", "default", "--width", "1400", "--height", "900") },
    @{ Name = "46-usage-unattributed-group.png"; Args = @("--page", "usage", "--account", "__unattributed__", "--width", "1250", "--height", "900") },
    @{ Name = "47-dashboard-1250-rail-and-currencies.png"; Args = @("--account", "default", "--width", "1250", "--height", "950") },
    @{ Name = "49-hover-after-model-reported.png"; Args = @("--page", "usage", "--account", "default", "--tip", "0", "--width", "1250", "--height", "900") },
    @{ Name = "51-hover-after-model-absent.png"; Data = $variant; Args = @("--page", "usage", "--account", "__unattributed__", "--expand", "0", "--tip", "0", "--tip-detail", "0", "--width", "1250", "--height", "900") },
    # 52-53  one row open, at the two widths the column widths are actually decided at:
    # 1918 is where 模型 is at its cap and the figures take the rest, 1250 is where 模型
    # is still absorbing everything. The expanded panel derives its columns from the task
    # row's, so these two frames are the acceptance for both at once: a sub-request's
    # 输入 must sit under the task's 输入 and its 费用 under the task's 费用, at both.
    @{ Name = "52-row-expanded-1918.png"; Args = @("--page", "usage", "--account", "default", "--expand", "0", "--width", "1918", "--height", "960") },
    @{ Name = "53-row-expanded-1250.png"; Args = @("--page", "usage", "--account", "default", "--expand", "0", "--width", "1250", "--height", "900") }
)

$log = Join-Path $shots "acceptance.log"
Set-Content -Path $log -Value "# capture-acceptance $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  data=$data" -Encoding utf8
foreach ($frame in $frames) {
    $png = Join-Path $shots $frame.Name
    if (Test-Path $png) { Remove-Item $png -Force }
    # -ProbeFile rather than a redirected standard output: this is a WinExe, and the
    # run's own numbers are what the notes are read off. One file per frame, appended to
    # the run's log under the frame's own name.
    $runLog = Join-Path $shots ("acceptance-" + [System.IO.Path]::GetFileNameWithoutExtension($frame.Name) + ".txt")
    if (Test-Path $runLog) { Remove-Item $runLog -Force }
    $frameData = if ($frame.ContainsKey("Data")) { $frame.Data } else { $data }
    $arguments = @("--data-dir", $frameData, "--capture", $png, "--probe-file", $runLog) + $frame.Args
    Write-Host ("--- {0}" -f $frame.Name)
    & $exe @arguments | Out-Null
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $png) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 150 }
    if (-not (Test-Path $png)) { throw "$($frame.Name) was not written" }
    Add-Content -Path $log -Value ("### {0}  {1}" -f $frame.Name, ($frame.Args -join " ")) -Encoding utf8
    if (Test-Path $runLog) { Get-Content $runLog -Encoding utf8 | Add-Content -Path $log -Encoding utf8 }
}

Write-Host ""
Write-Host "screenshots:"
Get-ChildItem $shots -Filter *.png | Sort-Object Name | ForEach-Object { Write-Host ("  {0}  {1} bytes" -f $_.FullName, $_.Length) }
