# 发布本地助手文件。默认构建 Frida 无插件版；只有显式传入 -DeployPlugin
# 或 -PluginOnly 时才构建并向游戏目录部署旧版 BepInEx 插件。
#
# 默认：
#   .\build-and-deploy.ps1 -InstallDir .\dist
# 插件兼容模式：
#   .\build-and-deploy.ps1 -GameDir "D:\SteamLibrary\steamapps\common\PC FISH" -DeployPlugin
# 仅更新游戏内插件：
#   .\build-and-deploy.ps1 -GameDir "..." -PluginOnly
param(
    [string]$GameDir = '',
    [string]$InstallDir = '',
    [switch]$PluginOnly,
    [switch]$DeployPlugin
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$deployLegacyPlugin = $PluginOnly -or $DeployPlugin

if (-not $InstallDir) { $InstallDir = Join-Path $root 'dist' }

if ($deployLegacyPlugin) {
    if (-not $GameDir) {
        $candidates = @(
            'D:\SteamLibrary\steamapps\common\PC FISH',
            'C:\Program Files (x86)\Steam\steamapps\common\PC FISH',
            'C:\Program Files\Steam\steamapps\common\PC FISH'
        )
        $GameDir = $candidates | Where-Object { Test-Path (Join-Path $_ 'PCFish.exe') } | Select-Object -First 1
    }
    if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir 'PCFish.exe'))) {
        throw '插件模式需要游戏目录，请用 -GameDir 指定含 PCFish.exe 的目录。'
    }
    if (-not (Test-Path (Join-Path $GameDir 'BepInEx\interop'))) {
        throw "找不到 BepInEx\interop：$GameDir\BepInEx。请先安装并启动过 BepInEx。"
    }
    if (Get-Process -Name PCFish -ErrorAction SilentlyContinue) {
        throw '游戏正在运行，插件 DLL 被占用。请先退出游戏。'
    }

    Write-Host "构建旧版插件：$GameDir" -ForegroundColor Cyan
    dotnet build (Join-Path $root 'pcfish-autohelper\AutoHelper.csproj') -c Release -p:GameDir=$GameDir --nologo |
        Select-Object -Last 4
    $plugin = Join-Path $root 'pcfish-autohelper\bin\Release\PCFishAutoHelper.dll'
    $pluginTarget = Join-Path $GameDir 'BepInEx\plugins\PCFishAutoHelper.dll'
    New-Item -ItemType Directory -Path (Split-Path -Parent $pluginTarget) -Force | Out-Null
    Copy-Item -LiteralPath $plugin -Destination $pluginTarget -Force
    Write-Host "插件已部署：$pluginTarget" -ForegroundColor Green

    if ($PluginOnly) {
        Write-Host '仅插件模式完成；未构建或覆盖桌面助手。' -ForegroundColor Green
        exit 0
    }
}

Write-Host '发布桌面助手与 Frida worker（默认无插件连接）…' -ForegroundColor Cyan
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
dotnet publish (Join-Path $root 'PCFishController\PCFishController.csproj') -c Release -o $InstallDir --nologo |
    Select-Object -Last 4
Copy-Item -LiteralPath (Join-Path $InstallDir 'PCFishController.exe') `
    -Destination (Join-Path $InstallDir 'PCFish助手.exe') -Force
dotnet publish (Join-Path $root 'PCFishFrida\PCFishFrida.csproj') -c Release -o $InstallDir --nologo |
    Select-Object -Last 4

if ($deployLegacyPlugin) {
    Copy-Item -LiteralPath (Join-Path $root 'pcfish-autohelper\bin\Release\PCFishAutoHelper.dll') `
        -Destination (Join-Path $InstallDir 'PCFishAutoHelper.dll') -Force
}

Write-Host "助手已发布到：$InstallDir" -ForegroundColor Green
Write-Host '默认连接方式为 Frida 内存连接：按 PID 附加游戏，不向游戏目录写入插件。' -ForegroundColor Cyan
if ($deployLegacyPlugin) {
    Write-Host '旧插件已作为兼容模式部署；可在助手设置中选择插件桥接。' -ForegroundColor Yellow
} else {
    Write-Host '如需插件兼容模式，再传 -GameDir <游戏目录> -DeployPlugin。' -ForegroundColor DarkGray
}
