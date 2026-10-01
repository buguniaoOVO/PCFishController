# 编译并部署 PCFish 助手（插件 + 控制器）
#
# 用法（PowerShell）：
#   powershell -ExecutionPolicy Bypass -File .\build-and-deploy.ps1 -GameDir "D:\SteamLibrary\steamapps\common\PC FISH"
#   powershell -ExecutionPolicy Bypass -File .\build-and-deploy.ps1 -GameDir "..." -PluginOnly
#
# 参数（都可省略，省略时会按默认值或从 Steam 常见路径里找一个）：
#   -GameDir     游戏根目录，含 PCFish.exe 和 BepInEx 文件夹
#   -InstallDir  控制器输出目录，默认 .\dist
#   -PluginOnly  只编译并部署插件，不处理控制器
param(
    [string]$GameDir = '',
    [string]$InstallDir = '',
    [switch]$PluginOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $GameDir) {
    $candidates = @(
        'D:\SteamLibrary\steamapps\common\PC FISH',
        'C:\Program Files (x86)\Steam\steamapps\common\PC FISH',
        'C:\Program Files\Steam\steamapps\common\PC FISH'
    )
    $GameDir = $candidates | Where-Object { Test-Path (Join-Path $_ 'PCFish.exe') } | Select-Object -First 1
    if (-not $GameDir) { throw '未找到游戏目录，请用 -GameDir 指定含 PCFish.exe 的路径。' }
}
if (-not (Test-Path (Join-Path $GameDir 'PCFish.exe'))) { throw "游戏目录里没有 PCFish.exe：$GameDir" }
if (-not (Test-Path (Join-Path $GameDir 'BepInEx\interop'))) {
    throw "找不到 BepInEx\interop：$GameDir\BepInEx。请先正确安装 BepInEx 并启动过游戏。"
}
if (-not $InstallDir) { $InstallDir = Join-Path $root 'dist' }

$game = Get-Process -Name PCFish -ErrorAction SilentlyContinue
if ($game) { throw "游戏正在运行（PID $($game.Id)），插件 DLL 被锁定。请先退出游戏。" }

Write-Host "游戏目录：$GameDir" -ForegroundColor Cyan

Write-Host '编译插件…' -ForegroundColor Cyan
dotnet build (Join-Path $root 'pcfish-autohelper\AutoHelper.csproj') -c Release -p:GameDir=$GameDir --nologo |
    Select-Object -Last 4

$plugin = Join-Path $root 'pcfish-autohelper\bin\Release\PCFishAutoHelper.dll'
$pluginTarget = Join-Path $GameDir 'BepInEx\plugins\PCFishAutoHelper.dll'
Copy-Item -LiteralPath $plugin -Destination $pluginTarget -Force
Write-Host "插件已部署：$pluginTarget" -ForegroundColor Green

$cfg = Join-Path $GameDir 'BepInEx\config\pcfish.autohelper.cfg'
if (Test-Path -LiteralPath $cfg) {
    $text = Get-Content -LiteralPath $cfg -Raw
    if ($text -match '允许外部执行动作\s*=\s*false') {
        $text = $text -replace '允许外部执行动作\s*=\s*false', '允许外部执行动作 = true'
        Set-Content -LiteralPath $cfg -Value $text -Encoding UTF8 -NoNewline
        Write-Host '已把游戏内 cfg 的「允许外部执行动作」改为 true' -ForegroundColor Green
    } else {
        Write-Host '游戏内 cfg 的「允许外部执行动作」已是 true（或字段名不同，请自行确认）'
    }
} else {
    Write-Host '尚未生成 cfg：先启动游戏一次，再运行本脚本或点控制器里的「一键部署」。' -ForegroundColor Yellow
}

if (-not $PluginOnly) {
    Write-Host '编译并发布控制器…' -ForegroundColor Cyan
    dotnet publish (Join-Path $root 'PCFishController\PCFishController.csproj') -c Release -o $InstallDir --nologo |
        Select-Object -Last 4
    $exe = Join-Path $InstallDir 'PCFishController.exe'
    Copy-Item -LiteralPath $exe -Destination (Join-Path $InstallDir 'PCFish助手.exe') -Force
    # 插件放一份到控制器旁边，这样界面里的「一键部署」能直接找到它。
    Copy-Item -LiteralPath $plugin -Destination (Join-Path $InstallDir 'PCFishAutoHelper.dll') -Force
    Write-Host "控制器已发布：$InstallDir\PCFish助手.exe" -ForegroundColor Green
}

Write-Host ''
Write-Host '下一步：' -ForegroundColor Cyan
Write-Host '  1. 启动游戏（BepInEx 会加载新插件）'
if (-not $PluginOnly) { Write-Host "  2. 双击 $InstallDir\PCFish助手.exe" }
Write-Host '  3. 在控制器上点 ARM，然后才可能自动繁育'
Write-Host '  提示：以后换了游戏目录或电脑，在控制器「设置 → 一键部署」里重新执行一次即可。'

