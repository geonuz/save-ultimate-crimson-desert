$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$baseline = Get-Content -Raw (Join-Path $root 'artifacts/editor-baseline.json') | ConvertFrom-Json
$expected = @{}
foreach ($file in $baseline.files) {
    $expected[$file.path] = $true
    $path = Join-Path $baseline.source $file.path
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
        throw "Original changed or missing: $($file.path)"
    }
}
Get-ChildItem -LiteralPath $baseline.source -Recurse -File -Force |
    Where-Object { $_.FullName -notlike "$($baseline.source)\.git\*" } |
    ForEach-Object {
        $relative = $_.FullName.Substring($baseline.source.TrimEnd('\').Length + 1)
        if (!$expected.ContainsKey($relative)) { throw "Original has an added file: $relative" }
    }
Write-Output "Original preserved: $($expected.Count) files match SHA-256 baseline."
