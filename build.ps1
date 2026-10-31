$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish .\Chisto.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
} finally { Pop-Location }
