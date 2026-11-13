param(
    [string]$DotNet = 'dotnet',
    [string]$DalamudHome = $env:DALAMUD_HOME,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/scripts/BuildPaths.ps1"
. "$PSScriptRoot/scripts/PackageNotices.ps1"
if ([string]::IsNullOrWhiteSpace($DalamudHome)) {
    $DalamudHome = Join-Path $env:APPDATA 'XIVLauncherCN/addon/Hooks/dev'
    if (!(Test-Path -LiteralPath (Join-Path $DalamudHome 'Dalamud.dll'))) {
        $DalamudHome = Join-Path $env:APPDATA 'XIVLauncher/addon/Hooks/dev'
    }
}
if (!(Test-Path -LiteralPath (Join-Path $DalamudHome 'Dalamud.dll'))) {
    throw '未找到 Dalamud.dll，请传入 -DalamudHome。'
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $pluginProject = Join-Path $PSScriptRoot 'src/DalamudMCP.Plugin/DalamudMCP.Plugin.csproj'
    $localOutput = & $DotNet msbuild $pluginProject "-p:DalamudLibPath=$DalamudHome" -getProperty:DalamudMCPOutputRoot -nologo
    if ($LASTEXITCODE -ne 0) { throw '无法读取插件输出配置。' }
    $OutputDirectory = ($localOutput -join '').Trim()
    if (!$OutputDirectory) {
        Assert-UpdateDirectory $PSScriptRoot
        $artifacts = Join-Path $PSScriptRoot 'artifacts'
        if (!(Test-Path -LiteralPath $artifacts)) { $null = New-Item -ItemType Directory -Path $artifacts }
        $OutputDirectory = Join-Path $artifacts 'DalamudMCP'
    }
}

$output = [IO.Path]::GetFullPath($OutputDirectory, $PSScriptRoot)
Assert-UpdateDirectory ([IO.Path]::GetDirectoryName($output))
if (!(Test-Path -LiteralPath $output)) { $null = New-Item -ItemType Directory -Path $output }
Assert-UpdateDirectory $output
$known = @('DalamudMCP.dll', 'DalamudMCP.json', 'DalamudMCP.deps.json', 'LICENSE', 'server', 'images')
$old = @(Get-ChildItem -LiteralPath $output -Force)
if ($old | Where-Object { $_.Name -notin $known }) { throw '交付目录中含不属于当前安装包的文件。' }
if ($old | Where-Object { $_.PSIsContainer -ne ($_.Name -in @('server', 'images')) }) { throw '交付目录结构不符。' }
if (Get-ChildItem -LiteralPath $output -Force -Recurse -Attributes ReparsePoint | Select-Object -First 1) {
    throw '交付目录不能包含链接。'
}
if ($old.Name -contains 'images' -and
    (Get-ChildItem -LiteralPath (Join-Path $output 'images') -Force | Where-Object { $_.PSIsContainer -or $_.Name -ne 'icon.png' })) {
    throw '交付目录的 images 中含不属于当前安装包的文件。'
}
if (Get-Process 'DalamudMCP.Server' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like ($output + '\*') }) {
    throw '交付目录里的网关正在运行。'
}

$work = Join-Path (Split-Path -Parent $output) ".dalamudmcp-build-$([guid]::NewGuid().ToString('N'))"
$package = Join-Path $work 'package'
$gateway = Join-Path $package 'server'
$backup = Join-Path $work 'previous'
$project = Join-Path $PSScriptRoot 'src/DalamudMCP.Server/DalamudMCP.Server.csproj'
$notices = Join-Path $work 'package-notices.txt'
$null = New-Item -ItemType Directory -Path $gateway, $backup
$keepBackup = $false
try {
    Write-PackageNotices $notices
    & $DotNet build "$PSScriptRoot/DalamudMCP.slnx" -c $Configuration --artifacts-path $work "-p:DalamudLibPath=$DalamudHome" "-p:PackageNoticesFile=$notices" -p:UseLocalOutput=false --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw '解决方案构建失败。' }
    & $DotNet publish $project -c $Configuration --artifacts-path $work --no-build --no-restore "-p:PackageNoticesFile=$notices" -p:UseLocalOutput=false -o $gateway --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw '网关发布失败。' }
    $gatewayFiles = @(Get-ChildItem -LiteralPath $gateway -Force)
    if ($gatewayFiles.Count -ne 1 -or $gatewayFiles[0].Name -cne 'DalamudMCP.Server.exe' -or $gatewayFiles[0].PSIsContainer) {
        throw '网关发布必须仅生成一个可执行文件。'
    }

    $plugin = Join-Path $work "bin/DalamudMCP.Plugin/$($Configuration.ToLowerInvariant())"
    foreach ($name in @('DalamudMCP.dll', 'DalamudMCP.json', 'DalamudMCP.deps.json')) {
        Copy-Item -LiteralPath (Join-Path $plugin $name) -Destination $package
    }
    $images = New-Item -ItemType Directory -Path (Join-Path $package 'images')
    Copy-Item -LiteralPath (Join-Path $plugin 'images/icon.png') -Destination $images.FullName
    Copy-Item -LiteralPath "$PSScriptRoot/LICENSE" -Destination $package

    $moved = @()
    $installed = @()
    try {
        foreach ($file in $old) {
            Move-Item -LiteralPath $file.FullName -Destination $backup
            $moved += $file.Name
        }
        foreach ($file in Get-ChildItem -LiteralPath $package -Force) {
            Move-Item -LiteralPath $file.FullName -Destination $output
            $installed += $file.Name
        }
    }
    catch {
        $keepBackup = $true
        foreach ($name in $installed) { Move-Item -LiteralPath (Join-Path $output $name) -Destination $package }
        foreach ($name in $moved) { Move-Item -LiteralPath (Join-Path $backup $name) -Destination $output }
        $keepBackup = $false
        throw
    }
    Write-Host "交付目录：$output"
}
finally {
    if (!$keepBackup) {
        Add-Type -AssemblyName Microsoft.VisualBasic.Core
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($work, 'OnlyErrorDialogs', 'SendToRecycleBin')
    }
}
