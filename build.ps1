#requires -Version 7
# 编 CLI 与游戏内导出器，落成安装目录 dist/
# -Jit：不做 Native AOT，产物需要装机的 .NET 10 运行时，不需要 MSVC 工具链

param([switch]$Jit)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$cli = Join-Path $root 'Sources\RimSearcher.Cli\RimSearcher.Cli.csproj'
$exporter = Join-Path $root 'Sources\RimSearcher.DataMod\RimSearcher.DataMod.csproj'
$staging = Join-Path $root 'obj\dist-staging'
$exporterOut = Join-Path $root 'obj\exporter'
$dist = Join-Path $root 'dist'

function Assert-Success([string]$what) {
    if ($LASTEXITCODE -ne 0) { throw "$what 失败（退出码 $LASTEXITCODE）" }
}

if (-not (Test-Path (Join-Path $root 'RimTooling\Sources'))) { throw '子模块 RimTooling 未取出，先执行 git submodule update --init' }

# Native AOT 的链接一步靠 vswhere 找 MSVC，VS 安装器不把它放进 PATH
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path (Join-Path $vswhere 'vswhere.exe')) { $env:PATH = "$vswhere;$env:PATH" }

foreach ($dir in $staging, $exporterOut) { if (Test-Path $dir) { Remove-Item $dir -Recurse -Force } }

$mode = $Jit ? @('-p:PublishAot=false', '--self-contained', 'false') : @()
dotnet publish $cli -c Release -r win-x64 -p:DebugType=none @mode -o $staging -nologo -v q
Assert-Success '编译 CLI'
Get-ChildItem $staging -Filter *.pdb | Remove-Item -Force

dotnet build $exporter -c Release -o $exporterOut -nologo -v q
Assert-Success '编译导出器'
Copy-Item (Join-Path $exporterOut 'RimSearcher.DataMod.dll') $staging
Copy-Item (Join-Path $root 'LICENSE') $staging

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
Move-Item $staging $dist
Write-Host "[dist] $dist"
exit 0
