param([switch]$Zip)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$projectRoot = (Get-Location).Path
Write-Host 'Unload Outfit Studio in Dalamud and stop conversions before updating release/.'
dotnet test tests/OutfitStudio.Tests/OutfitStudio.Tests.csproj -c Release
if ($LASTEXITCODE) { throw 'Tests failed' }
dotnet build src/OutfitStudio.Plugin/OutfitStudio.Plugin.csproj -c Release
if ($LASTEXITCODE) { throw 'Plugin build failed' }

$stage = Join-Path $projectRoot ('.release-stage-' + [Guid]::NewGuid().ToString('N'))
$release = Join-Path $projectRoot 'release'
$backup = Join-Path $projectRoot ('.release-backup-' + [Guid]::NewGuid().ToString('N'))
$publishLock = Join-Path $projectRoot '.release-publish.lock'
$temporaryZip = $null
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    # A fresh worker directory prevents obsolete dependencies surviving an update.
    $worker = Join-Path $stage 'worker'
    dotnet publish src/OutfitStudio.Worker/OutfitStudio.Worker.csproj -c Release -r win-x64 --self-contained true -o $worker
    if ($LASTEXITCODE) { throw 'Worker publish failed; existing release is unchanged' }
    foreach ($name in @('OutfitStudio.Worker.exe', 'OutfitStudio.Worker.dll',
            'OutfitStudio.Worker.deps.json', 'OutfitStudio.Worker.runtimeconfig.json',
            'OutfitStudio.Core.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll',
            'System.Private.CoreLib.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $worker $name) -PathType Leaf)) {
            throw "Incomplete self-contained worker: missing $name; existing release is unchanged"
        }
    }
    # A stale publish directory must never put symbols or local source paths in
    # the public ZIP. Release builds also disable debug information in the DLLs.
    Get-ChildItem -LiteralPath $worker -File -Recurse -Force |
        Where-Object { $_.Extension -ieq '.pdb' } | Remove-Item -Force

    $dependencies = Get-Content -LiteralPath (Join-Path $worker 'OutfitStudio.Worker.deps.json') -Raw | ConvertFrom-Json
    $runtimeVersions = @($dependencies.libraries.PSObject.Properties.Name |
        Where-Object { $_.StartsWith('runtimepack.Microsoft.NETCore.App.Runtime.win-x64/') } |
        ForEach-Object { $_.Split('/', 2)[1] })
    if ($runtimeVersions.Count -ne 1) { throw 'Expected exactly one Windows x64 .NET runtime pack in worker dependencies' }
    $runtimeVersion = $runtimeVersions[0]
    $runtimeConfiguration = Get-Content -LiteralPath (Join-Path $worker 'OutfitStudio.Worker.runtimeconfig.json') -Raw | ConvertFrom-Json
    $frameworkVersions = @($runtimeConfiguration.runtimeOptions.includedFrameworks |
        Where-Object { $_.name -eq 'Microsoft.NETCore.App' } | ForEach-Object { $_.version })
    if ($frameworkVersions.Count -ne 1 -or $frameworkVersions[0] -ne $runtimeVersion) {
        throw 'Worker runtime configuration and dependency versions differ'
    }
    $publicDocuments = @('README.md', 'CHANGELOG.md', 'docs/VERIFICATION.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')
    foreach ($component in @('dotnet-runtime', 'dotnet-host')) {
        $noticeDirectory = Join-Path $projectRoot "licenses/$component"
        $noticeVersion = (Get-Content -LiteralPath (Join-Path $noticeDirectory 'version.txt') -Raw).Trim()
        if ($noticeVersion -ne $runtimeVersion) {
            throw "$component notices are for $noticeVersion, but the published runtime is $runtimeVersion"
        }
        foreach ($notice in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT', 'version.txt')) {
            $publicDocuments += "licenses/$component/$notice"
        }
    }
    Copy-Item src/OutfitStudio.Plugin/bin/Release/OutfitStudio.dll $stage
    Copy-Item src/OutfitStudio.Plugin/bin/Release/OutfitStudio.json $stage
    foreach ($relativePath in $publicDocuments) {
        $destination = Join-Path $stage $relativePath
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $projectRoot $relativePath) -Destination $destination
    }
    if ($Zip) {
        [xml]$pluginProject = Get-Content src/OutfitStudio.Plugin/OutfitStudio.Plugin.csproj
        $version = $pluginProject.Project.PropertyGroup.Version
        $artifacts = Join-Path $projectRoot 'artifacts'
        New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
        $archive = Join-Path $artifacts "OutfitStudio-$version.zip"
        $temporaryZip = Join-Path $artifacts ('.package-' + [Guid]::NewGuid().ToString('N') + '.zip')
        Compress-Archive -Path "$stage/*" -DestinationPath $temporaryZip
    }

    $current = Get-Item -LiteralPath $release -Force -ErrorAction SilentlyContinue
    if ($null -ne $current -and (-not $current.PSIsContainer -or
            ($current.Attributes -band [IO.FileAttributes]::ReparsePoint))) {
        throw 'release must be a regular directory, not a link or file'
    }
    try {
        New-Item -ItemType Directory -Path $publishLock | Out-Null
    } catch {
        throw 'Cannot acquire release update lock. If another update was interrupted, remove .release-publish.lock after it has stopped; otherwise check folder write permissions.'
    }
    $handles = New-Object 'System.Collections.Generic.List[System.IDisposable]'
    $movedOld = $false
    try {
        if (Test-Path -LiteralPath $release) {
            # Share-delete permits our directory rename, while read/write sharing
            # is denied so files held by the plugin/worker reject the update first.
            foreach ($file in Get-ChildItem -LiteralPath $release -File -Recurse -Force) {
                $handles.Add([IO.File]::Open($file.FullName, [IO.FileMode]::Open,
                    [IO.FileAccess]::ReadWrite, [IO.FileShare]::Delete))
            }
            [IO.Directory]::Move($release, $backup)
            $movedOld = $true
        }
        try {
            [IO.Directory]::Move($stage, $release)
        } catch {
            if ($movedOld) { [IO.Directory]::Move($backup, $release) }
            throw
        }
    } catch {
        $recovery = if (Test-Path -LiteralPath $backup) { " Previous files are preserved at $backup." } else { '' }
        throw "Release update failed. Unload Outfit Studio in Dalamud, stop any conversion worker, and retry. Check folder write permissions if it still fails.$recovery Cause: $_"
    } finally {
        foreach ($handle in $handles) { $handle.Dispose() }
        Remove-Item -LiteralPath $publishLock
    }
    if ($movedOld) {
        try { Remove-Item -LiteralPath $backup -Recurse -Force }
        catch { Write-Warning "Release updated; old files remain at $backup. Unload the plugin and stop its worker before removing that backup." }
    }
    Write-Host "Dalamud dev plugin location: $(Join-Path $release 'OutfitStudio.dll')"
    if ($Zip) {
        Move-Item -LiteralPath $temporaryZip -Destination $archive -Force
        Write-Host "Optional ZIP: $archive"
    }
} finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
    if ($temporaryZip -and (Test-Path -LiteralPath $temporaryZip)) { Remove-Item -LiteralPath $temporaryZip -Force }
}
