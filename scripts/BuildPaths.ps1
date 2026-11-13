function Assert-UpdateDirectory([string]$Path) {
    $dir = Get-Item -LiteralPath $Path -Force
    if (!$dir.PSIsContainer) { throw "不是目录：$Path" }
    while ($dir) {
        if ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "不支持链接目录：$($dir.FullName)" }
        $dir = $dir.Parent
    }
}
