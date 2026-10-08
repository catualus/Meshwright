<#
.SYNOPSIS
    Packages Meshwright as a Compile Pal plugin.

.DESCRIPTION
    Produces artifacts/Meshwright/, a folder a user drops into their Compile Pal/Plugins directory.
    Compile Pal discovers it from the meta.json alone - there is nothing to register and no build of
    Compile Pal involved, which is the point: someone who does not want this simply does not have the
    folder, and the compile step is not there.

    Published self-contained and single-file so the folder is a handful of files rather than two
    hundred, and so it runs on a machine with no .NET installed. Compile Pal itself ships
    self-contained, so its users have no reason to have a runtime.

    Trimmed as well, which takes the executable from 95 MB to 22 MB. Safe here because nothing in this
    codebase resolves a type by name: the one package reference, SharpCompress, is used for exactly one
    thing - constructing an LZMA decoder directly - and a trimmed publish produces no warnings. That
    last part is the check to repeat if a dependency is ever added, because trimming away something
    reached only by reflection fails at run time on the one map that needs it, not at build time.

    Compressed, which is what lets it run under Wine - and takes it from 21 MB to 15 MB. An
    uncompressed single-file bundle has the runtime map System.Private.CoreLib.dll straight out of
    the executable at an offset inside it, and Wine refuses that mapping: "Failed to load
    System.Private.CoreLib.dll ... Incorrect alignment (0x8007046C)", before any of Meshwright runs.
    A compressed bundle is unpacked into memory instead, so there is nothing to map. Measured on
    Windows the cost is noise - 0.44 s to start against 0.41 s - and nothing is written to disk, which
    is what IncludeAllContentForSelfExtract (the other way round it) would have done. Shipwright is
    built the same way and already ran under Wine for that reason.

.PARAMETER Zip
    Also write artifacts/Meshwright-plugin.zip, which is the form to attach to a release.

.PARAMETER Version
    Stamps the executable with a version. Left off, the build carries whatever the SDK defaults to,
    which is fine for a local build and not fine for something attached to a release: the release
    workflow passes the tag through here so one number is the source of both.
#>
[CmdletBinding()]
param(
    [switch]$Zip,

    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$staging = Join-Path $root 'artifacts/publish'
$out = Join-Path $root 'artifacts/Meshwright'

# The folder name is load-bearing: Compile Pal matches it against meta.json's "Name" to decide whether
# a step is already registered, and a mismatch loads the plugin under a name its parameters do not
# belong to.
$source = Join-Path $root 'CompilePalPlugin/Meshwright'

Write-Host 'Publishing meshwright...' -ForegroundColor Cyan

# Built as one array rather than a backtick-continued line so the optional -p:Version can be added
# or left out without the call having two shapes. It has to be typed [string[]]: PowerShell unrolls a
# one-element array back to a bare string, and splatting a string passes it one character at a time.
[string[]]$publishArgs = @(
    'publish'
    (Join-Path $root 'MeshwrightCli/MeshwrightCli.csproj')
    '--configuration', 'Release'
    '--runtime', 'win-x64'
    '--self-contained', 'true'
    '-p:PublishSingleFile=true'
    '-p:PublishTrimmed=true'
    '-p:TrimMode=full'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    # Needed to run under Wine at all - see the header.
    '-p:EnableCompressionInSingleFile=true'
    '-warnaserror'
    '--output', $staging
)

if ($Version) { $publishArgs += "-p:Version=$Version" }

dotnet @publishArgs

if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

# Emptied in place rather than deleted and recreated.
#
# The folder is often not just build output: pointing Compile Pal's Plugins/Meshwright at it with a
# junction is the obvious way to test a build, and Compile Pal builds that predate keeping step state
# in their own settings save whether the step is ticked, and where it sits in the order, into the
# plugin's own meta.json. Deleting the folder
# and copying the repo's meta.json back threw that away on every build, so the step unticked itself.
#
# So the files are cleared but the folder stays, and those two answers are carried over from the
# meta.json being replaced. Text substitution rather than a JSON round trip so the shipped file keeps
# its own formatting.
$kept = @{}
$installedMeta = Join-Path $out 'meta.json'

if (Test-Path $installedMeta) {
    try {
        $previous = Get-Content $installedMeta -Raw | ConvertFrom-Json
        foreach ($field in 'DoRun', 'Order') {
            if ($null -ne $previous.$field) { $kept[$field] = $previous.$field }
        }
    }
    catch {
        Write-Warning "Could not read the existing meta.json, so its DoRun/Order are not carried over: $_"
    }
}

if (Test-Path $out) { Get-ChildItem $out -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

Copy-Item (Join-Path $staging 'meshwright.exe') $out

# Not the .pdb: it is larger than the executable and a plugin folder is something people copy around.
$meta = Get-Content (Join-Path $source 'meta.json') -Raw

foreach ($field in $kept.Keys) {
    $value = if ($kept[$field] -is [bool]) { "$($kept[$field])".ToLowerInvariant() }
             else { [string]::Format([cultureinfo]::InvariantCulture, '{0}', $kept[$field]) }
    $meta = $meta -replace "(`"$field`"\s*:\s*)[^,\r\n}]+", "`${1}$value"
}

Set-Content -Path $installedMeta -Value $meta -NoNewline -Encoding utf8
Copy-Item (Join-Path $source 'parameters.json') $out
Copy-Item (Join-Path $root 'LICENSE') $out
Copy-Item (Join-Path $source 'README.md') $out -ErrorAction SilentlyContinue

$size = (Get-ChildItem $out -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB

Write-Host ''
Write-Host "Plugin written to $out ($([math]::Round($size, 1)) MB)" -ForegroundColor Green
Get-ChildItem $out | ForEach-Object { Write-Host "  $($_.Name)" }

if ($Zip) {
    $archive = Join-Path $root 'artifacts/Meshwright-plugin.zip'
    if (Test-Path $archive) { Remove-Item $archive -Force }

    # Compressing the folder itself, not its contents, so the zip contains a Meshwright/ directory -
    # extracting it straight into Plugins/ then lands in the right place.
    Compress-Archive -Path $out -DestinationPath $archive
    Write-Host "Archive written to $archive" -ForegroundColor Green
}

Write-Host ''
Write-Host 'To install: copy the Meshwright folder into your Compile Pal "Plugins" directory,'
Write-Host 'then restart Compile Pal and add the Meshwright step to a preset.'
