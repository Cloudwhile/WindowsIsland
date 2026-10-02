[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$native = Join-Path $workspace 'native'
$output = Join-Path $workspace 'artifacts/native'
& cmake -S $native -B $output -A x64
if ($LASTEXITCODE) { throw 'Native hook configuration failed.' }
& cmake --build $output --config Release --parallel
if ($LASTEXITCODE) { throw 'Native hook build failed.' }
