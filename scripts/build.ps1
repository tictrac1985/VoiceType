# أمر واحد: بناء الحل + تشغيل الاختبارات + إنتاج البرنامج الجاهز في release\VoiceType
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "== Build ==" -ForegroundColor Cyan
dotnet build VoiceType.sln -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $SkipTests) {
    Write-Host "== Tests ==" -ForegroundColor Cyan
    dotnet test VoiceType.sln -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# البرنامج نفسه: مجلد نظيف يمكن نقله على فلاشة
$out = Join-Path $root "release\VoiceType"
$tools = Join-Path $root "release\dev-tools"
$common = @("-c", $Configuration, "-r", "win-x64", "--self-contained", "false", "-p:PublishSingleFile=true", "-p:DebugType=none")

Write-Host "== Publish app to $out ==" -ForegroundColor Cyan
dotnet publish src\VoiceType.App @common -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "== Publish developer tools (Lab + CLI) to $tools ==" -ForegroundColor Cyan
foreach ($project in @("tools\VoiceType.Lab", "tools\VoiceType.Cli")) {
    dotnet publish $project @common -o $tools
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# نحتفظ بملفات Windows x64 فقط
foreach ($folder in @($out, $tools)) {
    Get-ChildItem (Join-Path $folder "runtimes") -Directory | Where-Object { $_.Name -ne "win-x64" -and $_.Name -ne "vulkan" } | Remove-Item -Recurse -Force
    Remove-Item (Join-Path $folder "runtimes\win-x64\native") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $folder "ggml-metal.metal") -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $folder "*.xml") -ErrorAction SilentlyContinue
}

Write-Host "Done: $out  (data\ is created next to VoiceType.exe on first run)" -ForegroundColor Green
