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

# The second step is metadata only. It runs the executable from the folder above rather than carrying
# its own copy, so it costs a few hundred bytes instead of another twenty two megabytes, and the two
# folders have to be installed together.
$stampSource = Join-Path $root 'CompilePalPlugin/Meshwright Stamp'
$stampOut = Join-Path $root 'artifacts/Meshwright Stamp'

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
    '-warnaserror'
    '--output', $staging
)

if ($Version) { $publishArgs += "-p:Version=$Version" }

dotnet @publishArgs

if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

Copy-Item (Join-Path $staging 'meshwright.exe') $out

# Not the .pdb: it is larger than the executable and a plugin folder is something people copy around.
Copy-Item (Join-Path $source 'meta.json') $out
Copy-Item (Join-Path $source 'parameters.json') $out
Copy-Item (Join-Path $root 'LICENSE') $out
Copy-Item (Join-Path $source 'README.md') $out -ErrorAction SilentlyContinue

if (Test-Path $stampOut) { Remove-Item $stampOut -Recurse -Force }
New-Item -ItemType Directory -Path $stampOut -Force | Out-Null
Copy-Item (Join-Path $stampSource '*') $stampOut

$size = (Get-ChildItem $out -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB

Write-Host ''
Write-Host "Plugin written to $out ($([math]::Round($size, 1)) MB)" -ForegroundColor Green
Get-ChildItem $out | ForEach-Object { Write-Host "  $($_.Name)" }

if ($Zip) {
    $archive = Join-Path $root 'artifacts/Meshwright-plugin.zip'
    if (Test-Path $archive) { Remove-Item $archive -Force }

    # Compressing the folders themselves, not their contents, so the zip contains Meshwright/ and
    # Meshwright Stamp/ directories - extracting it straight into Plugins/ then lands in the right
    # place, with both steps installed together.
    Compress-Archive -Path $out, $stampOut -DestinationPath $archive
    Write-Host "Archive written to $archive" -ForegroundColor Green
}

Write-Host ''
Write-Host 'To install: copy the Meshwright folder into your Compile Pal "Plugins" directory,'
Write-Host 'then restart Compile Pal and add the Meshwright step to a preset.'
