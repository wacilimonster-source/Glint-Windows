param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [switch]$UseInstalledRuntime,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$windowsRoot = Split-Path $PSScriptRoot -Parent
$projectPath = Join-Path $windowsRoot 'Glint.Windows/Glint.Windows.csproj'
$dist = if ($OutputDirectory) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory) }
    else { Join-Path $windowsRoot "dist/Glint-0.1.6-$Runtime" }
if ($UseInstalledRuntime) {
    if ($Runtime -ne 'win-x64') { throw 'Installed runtime packaging requires this x64 host.' }
    & dotnet publish $projectPath -c Release -r $Runtime --self-contained false -p:NuGetAudit=false --source (Join-Path $env:USERPROFILE '.nuget/packages') -p:DebugType=None -o $dist --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $dotnetRoot = Split-Path (Get-Command dotnet).Source -Parent
    $runtimeVersion = Get-ChildItem (Join-Path $dotnetRoot 'shared/Microsoft.WindowsDesktop.App') -Directory | Where-Object { $_.Name -like '10.0.*' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
    foreach ($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
        $source = Join-Path $dotnetRoot "shared/$framework/$runtimeVersion"
        $licenses = Join-Path $dist "Data/DotNetLicenses/$framework"
        New-Item -ItemType Directory -Path $licenses -Force | Out-Null
        Get-ChildItem -LiteralPath $source -File | ForEach-Object {
            if ($_.Name -match 'LICENSE|THIRD-PARTY') { Copy-Item -LiteralPath $_.FullName -Destination $licenses }
            else { Copy-Item -LiteralPath $_.FullName -Destination $dist -Force }
        }
    }
    Copy-Item -LiteralPath (Join-Path $dotnetRoot "host/fxr/$runtimeVersion/hostfxr.dll") -Destination $dist
    $configPath = Join-Path $dist 'Glint.runtimeconfig.json'
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $config.runtimeOptions.PSObject.Properties.Remove('frameworks')
    $config.runtimeOptions.PSObject.Properties.Remove('framework')
    $config.runtimeOptions | Add-Member -NotePropertyName includedFrameworks -NotePropertyValue @(@{name='Microsoft.NETCore.App';version=$runtimeVersion},@{name='Microsoft.WindowsDesktop.App';version=$runtimeVersion}) -Force
    $config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $configPath -Encoding utf8
    # This dependency-free application uses an app-local runtime closure. The framework-
    # dependent deps manifest omits CoreCLR; without it the host probes the local DLLs.
    Remove-Item -LiteralPath (Join-Path $dist 'Glint.deps.json')
    $oldManifest = Join-Path $dist 'Glint.deps.original'
    if (Test-Path -LiteralPath $oldManifest) { Remove-Item -LiteralPath $oldManifest }
} else {
    & dotnet publish $projectPath -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o $dist --nologo
}
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
Copy-Item -LiteralPath (Join-Path $windowsRoot 'README.md') -Destination (Join-Path $dist 'README.md')
$archive = $dist + '.zip'
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $archive -Force
if ($Runtime -eq 'win-x64') {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($component in @('WeTypeSink.dll', 'WeTypeSink32.dll', 'WeTypeHook32.exe')) {
            $entry = $zip.GetEntry($component)
            if ($null -eq $entry -or $entry.Length -eq 0) { throw "Windows ZIP 缺少 $component。" }
        }
    }
    finally { $zip.Dispose() }
}
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($archive + '.sha256') -Value ($hash + '  ' + (Split-Path $archive -Leaf)) -Encoding ascii
Write-Output $archive
Write-Output "SHA256: $hash"
