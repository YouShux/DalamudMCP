function Write-PackageNotices([string]$OutputPath) {
    $root = Split-Path -Parent $PSScriptRoot
    $packetSources = Get-Content -LiteralPath "$PSScriptRoot/packet-sources.lock.json" -Raw | ConvertFrom-Json
    $dependencies = (Get-Content -LiteralPath "$root/src/DalamudMCP.Server/packages.lock.json" -Raw | ConvertFrom-Json).dependencies.'net10.0'
    if ($dependencies.'ModelContextProtocol.AspNetCore'.resolved -ne '1.1.0' -or
        $dependencies.ModelContextProtocol.resolved -ne '1.1.0' -or
        $dependencies.'ModelContextProtocol.Core'.resolved -ne '1.1.0' -or
        $dependencies.'Microsoft.Extensions.AI.Abstractions'.resolved -ne '10.3.0') {
        throw '网关依赖版本已变化，请更新打包许可的上游版本和校验值。'
    }

    $sdk = 'modelcontextprotocol/csharp-sdk/ca040d77ec885fc7ac7649487722adaf33a4fd3f'
    $elpis = "$($packetSources.structureSource.repository)/$($packetSources.structureSource.revision)"
    $aliases = $packetSources.aliasSource
    $sources = @(
        @{ Name = 'BossmodReborn'; Path = 'FFXIV-CombatReborn/BossmodReborn/75505283eae18a7694f55f3c5f590b7125866d56/LICENSE'; Hash = '9b75712108201bf7d31a976a913cc34dfe6e33bb647e93897ff90166d911bd3f' }
        @{ Name = 'MCP C# SDK'; Path = "$sdk/LICENSE"; Hash = '7c6a686d2b34b86ad8c726e0ac4d76903b525f251d73e617f8db3879fa8f11fc' }
        @{ Name = 'MCP C# SDK notices'; Path = "$sdk/THIRD-PARTY-NOTICES.txt"; Hash = 'b9c35f6ab075077d5c2b644fdc58eb5696b8b70f539947f9e1e57331599fc5ce' }
        @{ Name = 'Microsoft.Extensions.AI.Abstractions'; Path = 'dotnet/extensions/1055487872e1fa424d49f70355405796f9c3a8e3/LICENSE'; Hash = '47f0ad8ba753eae5de6178291cd8f38bbb42d2368b177f8e11714ad17c701275' }
        @{ Name = 'Elpis network and struct'; Path = "$elpis/packages/network/LICENSE"; Hash = '3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986' }
        @{ Name = 'FFXIVClientStructs attribution from Elpis'; Path = "$elpis/packages/network/FFXIVClientStructs.LICENSE"; Hash = '1b1c1c2bccd47a893cbe1d201c51d7a2d9ed5229b00a476b3486186fd146b600' }
        @{ Name = 'ffxiv-opcode-worker'; Path = "$($aliases.repository)/$($aliases.revision)/LICENSE"; Hash = $aliases.licenseSha256 }
        @{ Name = 'FFXIVOpcodes'; Path = 'karashiiro/FFXIVOpcodes/e57eacf21496479aa7e051fb69969a3a3b20569b/LICENSE'; Hash = '46c6713a0c3f9c945f3d87bd42c8b404b30790bb7725631cb34b4106497a93e7' }
    )

    $sections = foreach ($source in $sources) {
        $url = "https://raw.githubusercontent.com/$($source.Path)"
        $response = Invoke-WebRequest -Uri $url -MaximumRedirection 0 -TimeoutSec 25 -ErrorAction Stop
        $bytes = $response.RawContentStream.ToArray()
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        if ($hash -ne $source.Hash) { throw "打包许可的校验值不匹配：$($source.Name) ($url)" }
        "----- $($source.Name) -----`n$url`n`n$([Text.Encoding]::UTF8.GetString($bytes).TrimEnd())"
    }
    [IO.File]::WriteAllText($OutputPath, ($sections -join "`n`n") + "`n", [Text.UTF8Encoding]::new($false))
}
