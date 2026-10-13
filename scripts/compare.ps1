# تشغيل أداة سطر الأوامر من الكود المصدري (للمطور).
# أمثلة:
#   .\scripts\compare.ps1 help
#   .\scripts\compare.ps1 providers list
#   .\scripts\compare.ps1 compare run --open
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
dotnet run --project tools\VoiceType.Cli -c Release -- @args
