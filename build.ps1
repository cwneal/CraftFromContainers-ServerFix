param(
    [Parameter(Mandatory=$true)]
    [string]$GamePath,
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"

function Find-CSharpCompiler {
    $candidates = @()

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        try {
            $install = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
            if ($install) {
                $candidates += Join-Path $install "MSBuild\Current\Bin\Roslyn\csc.exe"
            }
        } catch {}
    }

    $candidates += @(
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe",
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
        "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    throw "Could not find csc.exe. Install Visual Studio 2022 Build Tools with .NET desktop build tools, or ensure .NET Framework csc.exe is present."
}

function Add-ReferenceIfExists([System.Collections.Generic.List[string]]$refs, [string]$path, [bool]$required = $true) {
    if (Test-Path $path) {
        $refs.Add($path)
    } elseif ($required) {
        throw "Required reference not found: $path"
    }
}

if (-not (Test-Path $GamePath)) {
    throw "GamePath not found: $GamePath"
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $scriptRoot "ReleaseLayout"
}

$managed = Join-Path $GamePath "7DaysToDie_Data\Managed"
$harmony = Join-Path $GamePath "Mods\0_TFP_Harmony\0Harmony.dll"

$refs = New-Object 'System.Collections.Generic.List[string]'
Add-ReferenceIfExists $refs (Join-Path $managed "Assembly-CSharp.dll")
Add-ReferenceIfExists $refs (Join-Path $managed "UnityEngine.CoreModule.dll")
# Harmony is required here (unlike the other two mods, where it's optional) - this mod does nothing
# useful without it.
Add-ReferenceIfExists $refs $harmony $true

$netstandardCandidates = @(
    (Join-Path $managed "netstandard.dll"),
    (Join-Path $managed "Facades\netstandard.dll"),
    "C:\Program Files\dotnet\packs\NETStandard.Library.Ref\2.1.0\ref\netstandard2.1\netstandard.dll",
    (Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\Facades\netstandard.dll")
)

$netstandard = $null
foreach ($candidate in $netstandardCandidates) {
    if ($candidate -and (Test-Path $candidate)) {
        $netstandard = $candidate
        break
    }
}
if ($netstandard) {
    Write-Host "Using netstandard reference: $netstandard"
    $refs.Add($netstandard)
}

$csc = Find-CSharpCompiler
Write-Host "Using compiler: $csc"
Write-Host "References:"
foreach ($r in $refs) { Write-Host "  $r" }

$modName = "0_CraftFromContainersServerFix"
$release = Join-Path $OutputRoot $modName
$srcFile = Join-Path $scriptRoot "src\CraftFromContainersServerFixRuntime.cs"
$dllOut = Join-Path $release "CraftFromContainersServerFix.dll"

if (Test-Path $release) { Remove-Item $release -Recurse -Force }
New-Item -ItemType Directory -Force -Path $release | Out-Null

$refArgs = @()
foreach ($r in $refs) { $refArgs += "/reference:$r" }

& $csc /target:library /nologo /optimize+ /out:$dllOut $refArgs $srcFile
if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

Copy-Item (Join-Path $scriptRoot "ModInfo.xml") (Join-Path $release "ModInfo.xml") -Force
if (Test-Path (Join-Path $scriptRoot "README.md")) {
    Copy-Item (Join-Path $scriptRoot "README.md") (Join-Path $release "README.md") -Force
}

Write-Host ""
Write-Host "Built: $dllOut"
Write-Host "Release folder: $release"
Write-Host "Server-side only - copy the whole $modName folder into your DEDICATED SERVER's Mods directory. It is not needed on player clients."
