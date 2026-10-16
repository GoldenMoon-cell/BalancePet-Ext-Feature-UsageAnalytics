# The "before" half of the hover pair.
#
#   pwsh -File tools\capture-tooltip-before.ps1
#
# The white empty box the user reported cannot be photographed from the fixed build, and
# it cannot be produced by checking out the previous commit either — the working tree
# carries a long list of uncommitted changes that must not be touched. So this script
# copies the source to a scratch directory under dist\, reverts exactly the two tooltip
# fixes in that copy, builds it and takes the two frames:
#
#   the App.xaml ToolTip style        -> the bubble goes back to the shell's pale box
#   MainWindow.Tip / Named            -> an empty value sets an empty tooltip again
#
# Nothing else differs between the two builds, which is what makes the pair a comparison
# of the fix rather than of two rounds of work. The frames are written next to the
# "after" ones, so the pair sorts together in the shots directory.
#
# The second frame needs a sub-request whose model nobody reported, which no captured
# dataset has: see the empty-model-detail variant this script builds beside the data.

[CmdletBinding()]
param(
    [string]$Output = "",
    [string]$Dataset = ""
)

$ErrorActionPreference = "Stop"
$repository = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
# repository -> dist\_ext-repos -> dist -> the workspace root
$workspace = [System.IO.Path]::GetFullPath((Join-Path $repository "..\..\.."))
if ([string]::IsNullOrWhiteSpace($Output)) { $Output = Join-Path $workspace "dist\usage-redesign-shots" }
$shots = [System.IO.Path]::GetFullPath($Output)
if ([string]::IsNullOrWhiteSpace($Dataset)) { $Dataset = Join-Path $workspace "dist\shots-data" }
$data = [System.IO.Path]::GetFullPath($Dataset)

# The variant the empty-tooltip frame needs: the captured dataset plus one task whose
# single sub-request reported no model. A relay that logs a request without naming a
# model is what the user hovered; no dataset on this machine happens to contain one, so
# one is appended rather than the frame being dropped. Same shape as the recharge
# variants in capture-redesign.ps1, and rebuilt on every run so a frame cannot be
# photographed against a state an earlier run left.
$variant = Join-Path $workspace "dist\ui-variants\empty-model-detail"
if (-not (Test-Path (Join-Path $variant "usage-events.ndjson"))) {
    if (Test-Path $variant) { Remove-Item $variant -Recurse -Force }
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

$scratch = Join-Path $workspace "dist\_perf-data\_tooltip-before"
if (Test-Path $scratch) { Remove-Item $scratch -Recurse -Force }
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
foreach ($item in @("src", "tools", "manifest.json", "CHANGELOG.md", "README.md")) {
    $source = Join-Path $repository $item
    if (Test-Path $source) { Copy-Item $source (Join-Path $scratch $item) -Recurse -Force }
}
Get-ChildItem (Join-Path $scratch "src") -Include bin, obj -Recurse -Directory | Remove-Item -Recurse -Force

# --- revert exactly the two fixes -------------------------------------------------
$appXaml = Join-Path $scratch "src\App.xaml"
$text = Get-Content $appXaml -Raw -Encoding utf8
$before = $text.Length
# The ToolTip style, from its opening tag to the first </Style> after it: the style has no
# nested styles, so the first one closes it.
$text = [regex]::Replace($text, '(?s)\s*<!-- =+\s*The hover bubble\..*?</Style>', '')
if ($text.Length -eq $before) { throw "the App.xaml ToolTip style was not found to revert" }
Set-Content -Path $appXaml -Value $text -Encoding utf8 -NoNewline

$mainCs = Join-Path $scratch "src\MainWindow.xaml.cs"
$text = Get-Content $mainCs -Raw -Encoding utf8
if (-not $text.Contains("Tip(block, value);")) { throw "the Named tooltip call was not found to revert" }
$text = $text.Replace("Tip(block, value);", "block.ToolTip = value;")
Set-Content -Path $mainCs -Value $text -Encoding utf8 -NoNewline

$project = Join-Path $scratch "src\BalancePet.UsageAnalytics.csproj"
$exe = Join-Path $scratch "src\bin\Release\net8.0-windows\BalancePet.UsageAnalytics.exe"
dotnet build $project -c Release --nologo -v:q | Out-Null
if (-not (Test-Path $exe)) { throw "No executable at $exe" }
Write-Host "before-build: $exe"

New-Item -ItemType Directory -Force -Path $shots | Out-Null
$frames = @(
    # A row whose model WAS reported: the bubble had text in it and the text was drawn in
    # the window's own near-white, on the shell's pale bubble — a white box with nothing
    # legible in it, which is the report.
    @{ Name = "48-hover-before-model-reported.png"; Data = $data; Args = @("--page", "usage", "--account", "default", "--tip", "0") },
    # A row whose sub-request model was NOT reported: ToolTip was set to "" and WPF opens a
    # bubble for an empty string, so this one is empty as well as illegible.
    @{ Name = "50-hover-before-model-absent.png"; Data = $variant; Args = @("--page", "usage", "--account", "__unattributed__", "--expand", "0", "--tip", "0", "--tip-detail", "0") }
)

foreach ($frame in $frames) {
    $png = Join-Path $shots $frame.Name
    if (Test-Path $png) { Remove-Item $png -Force }
    $runLog = Join-Path $shots ("acceptance-" + [System.IO.Path]::GetFileNameWithoutExtension($frame.Name) + ".txt")
    if (Test-Path $runLog) { Remove-Item $runLog -Force }
    $arguments = @("--data-dir", $frame.Data, "--capture", $png, "--probe-file", $runLog, "--width", "1250", "--height", "900") + $frame.Args
    Write-Host ("--- {0}  ({1})" -f $frame.Name, ($arguments -join " "))
    & $exe @arguments | Out-Null
    $deadline = (Get-Date).AddSeconds(20)
    while (-not (Test-Path $png) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 150 }
    if (-not (Test-Path $png)) { throw "$($frame.Name) was not written" }
    if (Test-Path $runLog) { Get-Content $runLog -Encoding utf8 | ForEach-Object { Write-Host "    $_" } }
}
