param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$RuntimeIdentifier
)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory.Trim().Trim('"').TrimEnd('\'))
$native = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Glint.Windows/Native'))
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$targets = @(
    @{ Arch = 'x64'; Vcvars = 'vcvars64.bat'; Source = 'WeTypeSink.c'; File = 'WeTypeSink.dll'; Flags = '/LD'; Libraries = 'oleaut32.lib user32.lib' }
)
if ($RuntimeIdentifier -ne 'win-arm64') {
    $targets += @(
        @{ Arch = 'x86'; Vcvars = 'vcvars32.bat'; Source = 'WeTypeSink.c'; File = 'WeTypeSink32.dll'; Flags = '/LD'; Libraries = 'oleaut32.lib user32.lib' },
        @{ Arch = 'x86'; Vcvars = 'vcvars32.bat'; Source = 'WeTypeHook32.c'; File = 'WeTypeHook32.exe'; Flags = ''; Libraries = 'user32.lib advapi32.lib shell32.lib' }
    )
}
$scriptTime = (Get-Item -LiteralPath $PSCommandPath).LastWriteTimeUtc
$pending = @($targets | Where-Object {
    $output = Get-Item -LiteralPath (Join-Path $OutputDirectory $_.File) -ErrorAction SilentlyContinue
    $source = Get-Item -LiteralPath (Join-Path $native $_.Source)
    -not $output -or $output.LastWriteTimeUtc -lt $source.LastWriteTimeUtc -or $output.LastWriteTimeUtc -lt $scriptTime
})
if ($pending.Count -eq 0) { exit 0 }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = $null
if (Test-Path -LiteralPath $vswhere) {
    $installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
if (-not $installation) {
    foreach ($candidate in @(
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise",
        "${env:ProgramFiles}\Microsoft Visual Studio\18\Community")) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'VC\Auxiliary\Build\vcvars64.bat')) { $installation = $candidate; break }
    }
}
if (-not $installation) { throw '未找到 Visual C++ 工具集，无法编译微信输入法桥接。' }
foreach ($target in $pending) {
    $vcvars = Join-Path $installation ('VC\Auxiliary\Build\' + $target.Vcvars)
    $file = $target.File
    $source = Join-Path $native $target.Source
    $work = Join-Path $env:TEMP ('glint-wetype-sink\' + $target.Arch)
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $built = Join-Path $work $file
    Push-Location $work
    try {
        $command = '"{0}" >nul && cl /nologo {1} /O2 /MT /W4 /WX /DUNICODE /D_UNICODE /utf-8 "{2}" /Fe{3} /link {4}' -f
            $vcvars, $target.Flags, $source, $file, $target.Libraries
        cmd /c $command
    }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $built)) { throw "微信输入法桥接编译失败 ($file)。" }
    Copy-Item -LiteralPath $built -Destination (Join-Path $OutputDirectory $file) -Force
}
