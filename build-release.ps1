param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'release'))
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$project = Join-Path $PSScriptRoot 'Chisto.csproj'
[xml]$xml = Get-Content -LiteralPath $project
$version = [string]$xml.Project.PropertyGroup.Version
$publish = Join-Path $OutputDirectory "Chisto-$version"
dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $publish -Force
$report = Join-Path $OutputDirectory 'test-results.json'
$test = Start-Process -FilePath (Join-Path $publish 'Chisto.exe') -ArgumentList @('--self-test',('"' + $report + '"')) -WindowStyle Hidden -Wait -PassThru
if ($test.ExitCode -ne 0) { throw 'Self-tests failed' }
$candidates = @((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),(Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),(Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'))
$compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $compiler) { $compiler = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $compiler) { throw 'Install Inno Setup 6 to build the installer.' }
& $compiler "/DMyAppSource=$publish" "/DMyAppVersion=$version" "/O$OutputDirectory" (Join-Path $PSScriptRoot 'Installer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
Compress-Archive -LiteralPath $publish -DestinationPath (Join-Path $OutputDirectory "Chisto-portable-$version.zip") -Force
$files = @(Join-Path $OutputDirectory "Chisto-Setup-$version.exe"; Join-Path $OutputDirectory "Chisto-portable-$version.zip")
$lines = foreach ($file in $files) { (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($file) }
$lines | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ASCII
Write-Output "Release $version ready at $OutputDirectory"
