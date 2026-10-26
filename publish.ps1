# 发布 VisioView 为 Windows x64 自包含单文件 exe。
# 用法（PowerShell）：  .\publish.ps1
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $root

Write-Host "==> 还原并发布 (Release, win-x64, 自包含单文件) ..." -ForegroundColor Cyan
dotnet publish src/VisioView/VisioView.csproj -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true

$out = "src\VisioView\bin\Release\net10.0-windows\win-x64\publish\VisioView.exe"
if (Test-Path $out) {
    $size = [math]::Round((Get-Item $out).Length / 1MB, 1)
    Write-Host "==> 完成。产物: $out  (${size} MB)" -ForegroundColor Green
    Write-Host "    可用 Inno Setup 打开 installer\VisioView.iss 制作安装包。" -ForegroundColor Gray
} else {
    Write-Host "==> 发布失败，未找到产物。" -ForegroundColor Red
    exit 1
}
