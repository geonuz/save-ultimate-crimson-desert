param(
    [string]$ToolchainDirectory,
    [switch]$Test,
    [switch]$Publish,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.tools/cli'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:NUGET_PACKAGES = Join-Path $root '.tools/packages'
    $dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
    $env:DOTNET_ROOT = Split-Path $dotnet
    if ($ToolchainDirectory) {
        $toolchain = (Resolve-Path -LiteralPath $ToolchainDirectory).Path
        $env:PATH = $toolchain + ';' + $env:PATH
    }
    $cmake = (Get-Command cmake -ErrorAction Stop).Source
    $configure = @('-S', 'native', '-B', 'native/build', '-DCMAKE_BUILD_TYPE=Release')
    if (Get-Command g++ -ErrorAction SilentlyContinue) { $configure += @('-G', 'MinGW Makefiles') }
    & $cmake @configure
    if ($LASTEXITCODE) { throw 'Native configuration failed.' }
    & $cmake --build native/build --config Release --target parc_validation parc_index parc_reader --parallel 4
    if ($LASTEXITCODE) { throw 'Native compilation failed.' }
    New-Item -ItemType Directory -Force native/bin | Out-Null
    foreach ($name in 'parc_validation', 'parc_index', 'parc_reader') {
        $binary = Join-Path $root "native/build/$name.dll"
        if (!(Test-Path -LiteralPath $binary)) { $binary = Join-Path $root "native/build/Release/$name.dll" }
        Copy-Item -LiteralPath $binary -Destination native/bin
    }
    & $dotnet restore SaveUltimate.slnx --locked-mode --disable-parallel -m:1
    if ($LASTEXITCODE) { throw 'Locked package restore failed.' }
    & $dotnet build SaveUltimate.slnx -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE) { throw 'Managed compilation failed.' }
    if ($Test) {
        & $dotnet test tests/SaveUltimate.Tests -c Release --no-build --no-restore -m:1 --logger trx --results-directory artifacts/tests
        if ($LASTEXITCODE) { throw 'Tests failed.' }
    }
    if ($Publish) {
        if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ('releases/SaveUltimate-' + (Get-Date -Format yyyyMMdd-HHmmss)) }
        # .NET resolves relative paths against the process directory, not the PowerShell location.
        $output = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $root $OutputDirectory }))
        if (Test-Path -LiteralPath $output) { throw 'Choose a new publication directory to avoid stale files.' }
        & $dotnet publish src/SaveUltimate.App -c Release -r win-x64 --self-contained true -o $output -p:RestoreLockedMode=true -p:UseSharedCompilation=false -p:DebugType=None -m:1
        if ($LASTEXITCODE) { throw 'Single-file publication failed.' }
        Copy-Item -LiteralPath LICENSE,LICENSES.md,README.md,CHANGELOG.md -Destination $output
        New-Item -ItemType Directory -Force -Path (Join-Path $output 'licenses') | Out-Null
        foreach ($notice in 'LICENSE.txt', 'ThirdPartyNotices.txt') {
            $sourceNotice = Join-Path (Split-Path $dotnet) $notice
            if (Test-Path -LiteralPath $sourceNotice) { Copy-Item -LiteralPath $sourceNotice -Destination (Join-Path $output "licenses/dotnet-$notice") }
        }
        foreach ($name in 'Upstream-MPL-2.0.txt','crimson-rs-MIT.txt','K4os-LZ4-MIT.txt','LZ4-native-BSD-2-Clause.txt','MinGW-w64-runtime.txt') {
            Copy-Item -LiteralPath (Join-Path $root "licenses/$name") -Destination (Join-Path $output 'licenses')
        }
        $assets = Get-Content -Raw src/SaveUltimate.App/obj/project.assets.json | ConvertFrom-Json
        foreach ($library in $assets.libraries.PSObject.Properties) {
            if ($library.Value.type -ne 'package') { continue }
            $package = Join-Path $env:NUGET_PACKAGES $library.Value.path
            $notices = Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '(?i)license|notice' }
            foreach ($notice in $notices) {
                $label = $library.Name.Replace('/', '-') + '-' + $notice.Name
                Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $output "licenses/$label")
            }
        }
        Copy-Item -LiteralPath src/SaveUltimate.App/packages.lock.json -Destination (Join-Path $output 'dependencies.lock.json')
        $files = @(Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName | ForEach-Object {
            [ordered]@{ path = $_.FullName.Substring($output.TrimEnd('\').Length + 1); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
        [ordered]@{ sdk = (& $dotnet --version); configuration = 'Release'; signed = $false; files = $files } |
            ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $output 'release-manifest.json')
        $all = Get-ChildItem -LiteralPath $output -File -Recurse
        Write-Output ("Published: {0}; {1} files; {2:N1} MiB; unsigned development build" -f $output, $all.Count, (($all | Measure-Object Length -Sum).Sum / 1MB))
    }
}
finally { Pop-Location }
