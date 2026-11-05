# Builds Tracedrop.exe on Windows: fetches the optional AI models, runs the tests, publishes one portable exe.
#   powershell -ExecutionPolicy Bypass -File build.ps1            # everything
#   powershell -ExecutionPolicy Bypass -File build.ps1 -SkipTests
# Needs the .NET 8 SDK (https://dotnet.microsoft.com/download). Python is optional: without it the models are
# downloaded directly and only size-checked instead of smoke-tested.
param([switch]$SkipTests, [switch]$SkipModels)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $SkipModels) {
    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        & python tools/models/fetch_models.py
    } else {
        $manifest = Get-Content tools/models/models.json -Raw | ConvertFrom-Json
        New-Item -ItemType Directory -Force -Path models | Out-Null
        foreach ($model in $manifest.models) {
            $target = Join-Path models $model.file
            if ((Test-Path $target) -or $model.committed) { continue }
            foreach ($url in $model.urls) {
                try {
                    Write-Host "Downloading $($model.file) from $url"
                    Invoke-WebRequest -Uri $url -OutFile "$target.part" -UseBasicParsing
                    if ((Get-Item "$target.part").Length -lt 1MB) { throw "file too small" }
                    if ($model.sha256 -and ((Get-FileHash "$target.part" -Algorithm SHA256).Hash.ToLower() -ne $model.sha256)) { throw "sha256 mismatch" }
                    Move-Item -Force "$target.part" $target
                    break
                } catch {
                    Write-Warning "$($model.file): $_"
                    Remove-Item -Force -ErrorAction SilentlyContinue "$target.part"
                }
            }
        }
    }
}

if (-not $SkipTests) {
    dotnet test Tracedrop.Core.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

dotnet publish TracedropApp -c Release -o dist
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
$exe = Get-Item dist/Tracedrop.exe
Write-Host ("Built {0} ({1:N0} MB)" -f $exe.FullName, ($exe.Length / 1MB))
