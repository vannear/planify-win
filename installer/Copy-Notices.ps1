param([Parameter(Mandatory=$true)][string]$PublishDir, [string]$PackagesDir = $env:NUGET_PACKAGES)
$ErrorActionPreference = 'Stop'
if (!$PackagesDir) { $PackagesDir = Join-Path $env:USERPROFILE '.nuget/packages' }
$root = Split-Path $PSScriptRoot -Parent
foreach ($name in @('LICENSE','README.md','README.zh-CN.md','THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination $PublishDir
}
$assets = Get-Content (Join-Path $root 'src/Planify.App/obj/project.assets.json') -Raw | ConvertFrom-Json
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $packagePath = Join-Path $PackagesDir $library.Value.path
    foreach ($file in Get-ChildItem -LiteralPath $packagePath -Recurse -File) {
        if ($file.Extension -notin @('.txt','.md','') -or $file.Name -notmatch 'license|thirdpartynotice|third-party-notice') { continue }
        $relative = [IO.Path]::GetRelativePath($PackagesDir, $file.FullName)
        $destination = Join-Path $PublishDir "third-party-licenses/$relative"
        New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}
