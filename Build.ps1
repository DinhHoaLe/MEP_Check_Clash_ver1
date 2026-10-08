param(
    [ValidateSet('2023','2024','2025')][string]$RevitVersion='2023',
    [ValidateSet('Debug','Release')][string]$Configuration='Release',
    [string]$RevitInstallDir="C:\Program Files\Autodesk\Revit $RevitVersion",
    [switch]$Install
)
$ErrorActionPreference='Stop'
# Some launcher environments contain both Path and PATH; Roslyn needs one entry.
$taskBuildPath=$env:Path
Remove-Item Env:PATH -ErrorAction SilentlyContinue
$env:Path=$taskBuildPath
if ($RevitVersion -eq '2025') {
    & dotnet build (Join-Path $PSScriptRoot 'MEP_Check_Clash_ver1.Revit2025.csproj') -c $Configuration --ignore-failed-sources --nologo -v minimal "/p:RevitInstallDir=$RevitInstallDir" /p:UseSharedCompilation=false
} else {
    $msbuild=Get-Command msbuild -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
    if (!$msbuild) {
        $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) { $msbuild= & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1 }
    }
    if (!$msbuild) { throw 'Install Visual Studio / Build Tools with .NET desktop development and the .NET Framework 4.8 targeting pack.' }
    & $msbuild (Join-Path $PSScriptRoot 'MEP_Check_Clash_ver1.csproj') /t:Build "/p:Configuration=$Configuration" "/p:RevitVersion=$RevitVersion" "/p:RevitInstallDir=$RevitInstallDir" /p:UseSharedCompilation=false /v:minimal /nologo
}
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
$output=Join-Path $PSScriptRoot "bin\$RevitVersion\$Configuration"
$assembly=Join-Path $output 'MEP_Check_Clash_ver1.dll'
$manifest=Join-Path $output 'ClashSolution.addin'
$xml=New-Object System.Xml.XmlDocument
$xml.LoadXml('<RevitAddIns><AddIn Type="Application"><Name>Clash Solution CSharp</Name><Assembly/><AddInId>9DDF35BD-8C17-4C78-A591-2B1CB29679D4</AddInId><FullClassName>MEP_Check_Clash_ver1.App</FullClassName><VendorId>CLSH</VendorId><VendorDescription>Clash Solution</VendorDescription></AddIn></RevitAddIns>')
$xml.RevitAddIns.AddIn.Assembly=$assembly
$xml.Save($manifest)
Write-Output "DLL: $assembly"
Write-Output "Manifest: $manifest"
if ($Install) {
    $destination=Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath $manifest -Destination (Join-Path $destination 'ClashSolution.CSharp.addin') -Force
    Write-Output "Installed manifest for Revit $RevitVersion. Restart Revit; keep this build folder in place."
}
