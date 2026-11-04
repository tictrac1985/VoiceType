# نشر نسخة جديدة من صدى على GitHub Releases، عشان البرنامج على أي جهاز يحدّث نفسه
# (الإعدادات ← التحديثات ← «دوّر على تحديث»).
#
# الاستخدام (من مجلد الكود):   powershell -ExecutionPolicy Bypass -File scripts\release.ps1
# قبلها: غيّر <Version> في Directory.Build.props، واعمل commit و push للكود.
# محتاج: git، و gh مسجل دخول بحساب صاحب المستودع، و .NET SDK 10.
param(
    [string]$Notes = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content Directory.Build.props
$version = $props.Project.PropertyGroup.Version
$tag = "v$version"
Write-Host "== صدى $tag ==" -ForegroundColor Cyan

# 1) الكود لازم يكون متسجّل ومرفوع: النسخة المنشورة لازم تطابق اللي على GitHub
if (git status --porcelain) { throw "فيه تعديلات مش متسجّلة (git status). اعمل commit الأول." }
git fetch -q origin
$ahead = git rev-list --count "origin/main..HEAD"
if ([int]$ahead -gt 0) { throw "فيه $ahead commit مش مرفوعين. اعمل git push الأول." }
if (git tag -l $tag) { throw "النسخة $tag منشورة قبل كده. زوّد Version في Directory.Build.props." }

# 2) الاختبارات
dotnet test tests\VoiceType.Tests -c Release
if ($LASTEXITCODE -ne 0) { throw "الاختبارات فشلت." }

# 3) البناء في مجلد مؤقت (مش مجلد البرنامج اللي شغال)
$out = Join-Path $env:TEMP "sada-release-$version"
$zip = Join-Path $env:TEMP "Sada-win-x64.zip"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $zip, "$zip.sha256" -Force -ErrorAction SilentlyContinue
dotnet publish src\VoiceType.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "البناء فشل." }
Remove-Item "$out\*.xml", "$out\*.metal" -ErrorAction SilentlyContinue
foreach ($required in @("VoiceType.exe", "crispasr\VoiceType.CrispHost.exe")) {
    if (-not (Test-Path (Join-Path $out $required))) { throw "ناقص $required في البناء." }
}
if (Test-Path (Join-Path $out "data")) { throw "البناء فيه مجلد data (بيانات مستخدم)! مش هننشره." }

# 4) الملف المضغوط وبصمته
# ZipFile بدل Compress-Archive: Compress-Archive في Windows PowerShell بيكتب المسارات بـ \ بدل / (مش قياسي)
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$check = [IO.Compression.ZipFile]::OpenRead($zip)
$bad = @($check.Entries | Where-Object { $_.FullName.Contains('\') }).Count
$check.Dispose()
if ($bad -gt 0) { throw "الملف المضغوط فيه مسارات بـ \ ($bad)." }
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
"$hash  Sada-win-x64.zip" | Out-File -Encoding ascii -NoNewline "$zip.sha256"
Write-Host "SHA-256: $hash"

# 5) النشر
if (-not $Notes) { $Notes = "صدى $tag" }
git tag -a $tag -m $Notes
git push -q origin $tag
gh release create $tag $zip "$zip.sha256" --title "صدى $tag" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "النشر على GitHub فشل." }
Write-Host "اتنشرت $tag ✔" -ForegroundColor Green
