param(
    [Parameter(Mandatory=$true)][string]$PublishDir,
    [string]$OutputDir = "$PSScriptRoot\..\artifacts",
    [string]$Version = '0.4.0',
    [string]$WixExe = 'wix',
    [string]$WixUI = 'WixToolset.UI.wixext'
)
$ErrorActionPreference = 'Stop'
$PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$generated = Join-Path $OutputDir 'installer-generated'
New-Item -ItemType Directory -Force -Path $generated | Out-Null
if (!(Test-Path -LiteralPath (Join-Path $PublishDir 'Planify.App.exe'))) { throw 'Publish the Windows application first.' }

function StableId([string]$prefix, [string]$relative) {
    $digest = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($relative.ToLowerInvariant()))
    return $prefix + [Convert]::ToHexString($digest).Substring(0, 32)
}
function StableGuid([string]$relative) {
    $hex = (StableId '' ('PlanifyWindowsCommunity/Files/' + $relative)).ToCharArray()
    $hex[12] = '8'; $hex[16] = '8'
    return ([Guid]::ParseExact((-join $hex), 'N')).ToString()
}
$ns = 'http://wixtoolset.org/schemas/v4/wxs'
$document = [Xml.XmlDocument]::new()
$wix = $document.CreateElement('Wix', $ns); $document.AppendChild($wix) | Out-Null
$fragment = $document.CreateElement('Fragment', $ns); $wix.AppendChild($fragment) | Out-Null
$directoryRef = $document.CreateElement('DirectoryRef', $ns); $directoryRef.SetAttribute('Id','INSTALLFOLDER'); $fragment.AppendChild($directoryRef) | Out-Null
$group = $document.CreateElement('ComponentGroup', $ns); $group.SetAttribute('Id','AppFiles'); $fragment.AppendChild($group) | Out-Null
$directories = @{ '.' = $directoryRef }
$files = Get-ChildItem -LiteralPath $PublishDir -Recurse -File | Where-Object { $_.Extension -notin @('.pdb','.db','.wixpdb') } | Sort-Object FullName
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($PublishDir, $file.FullName)
    $parent = '.'
    $pieces = $relative.Split([IO.Path]::DirectorySeparatorChar)
    for ($i=0; $i -lt $pieces.Length-1; $i++) {
        $subpath = if ($parent -eq '.') { $pieces[$i] } else { Join-Path $parent $pieces[$i] }
        if (!$directories.ContainsKey($subpath)) {
            $dir = $document.CreateElement('Directory',$ns); $dir.SetAttribute('Id',(StableId 'D' $subpath)); $dir.SetAttribute('Name',$pieces[$i])
            $directories[$parent].AppendChild($dir) | Out-Null; $directories[$subpath] = $dir
        }
        $parent = $subpath
    }
    $componentId = StableId 'C' $relative
    $component = $document.CreateElement('Component',$ns); $component.SetAttribute('Id',$componentId); $component.SetAttribute('Guid',(StableGuid $relative))
    $fileElement = $document.CreateElement('File',$ns); $fileElement.SetAttribute('Id',(StableId 'F' $relative)); $fileElement.SetAttribute('Source',$file.FullName)
    $key = $document.CreateElement('RegistryValue',$ns); $key.SetAttribute('Root','HKCU'); $key.SetAttribute('Key','Software\PlanifyWindowsCommunity\Installer\Files'); $key.SetAttribute('Name',$componentId); $key.SetAttribute('Type','integer'); $key.SetAttribute('Value','1'); $key.SetAttribute('KeyPath','yes'); $component.AppendChild($key) | Out-Null
    $component.AppendChild($fileElement) | Out-Null; $directories[$parent].AppendChild($component) | Out-Null
    $componentRef = $document.CreateElement('ComponentRef',$ns); $componentRef.SetAttribute('Id',$componentId); $group.AppendChild($componentRef) | Out-Null
}
foreach ($entry in $directories.GetEnumerator()) {
    $cleanup = $document.CreateElement('Component',$ns); $cleanup.SetAttribute('Id',(StableId 'Cleanup' $entry.Key)); $cleanup.SetAttribute('Guid','*')
    $key = $document.CreateElement('RegistryValue',$ns); $key.SetAttribute('Root','HKCU'); $key.SetAttribute('Key','Software\PlanifyWindowsCommunity\Installer\Directories'); $key.SetAttribute('Name',(StableId 'D' $entry.Key)); $key.SetAttribute('Type','integer'); $key.SetAttribute('Value','1'); $key.SetAttribute('KeyPath','yes'); $cleanup.AppendChild($key) | Out-Null
    $remove = $document.CreateElement('RemoveFolder',$ns); $remove.SetAttribute('Id',(StableId 'Remove' $entry.Key)); $remove.SetAttribute('On','uninstall'); $cleanup.AppendChild($remove) | Out-Null
    $entry.Value.AppendChild($cleanup) | Out-Null
    $reference = $document.CreateElement('ComponentRef',$ns); $reference.SetAttribute('Id',$cleanup.GetAttribute('Id')); $group.AppendChild($reference) | Out-Null
}
$filesSource = Join-Path $generated 'Files.wxs'; $document.Save($filesSource)
$license = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\LICENSE')).Replace('\','\\').Replace('{','\{').Replace('}','\}').Replace("`r",'').Replace("`n",'\par ')
$licenseRtf = Join-Path $generated 'License.rtf'
[IO.File]::WriteAllText($licenseRtf, '{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs18 ' + $license + '}', [Text.Encoding]::ASCII)
$installer = Join-Path $OutputDir "Planify-Windows-$Version-x64.msi"
& $WixExe build (Join-Path $PSScriptRoot 'Package.wxs') $filesSource -arch x64 -ext $WixUI -d "Version=$Version" -d "PublishDir=$PublishDir" -d "LicenseRtf=$licenseRtf" -pdbtype none -o $installer
if ($LASTEXITCODE -ne 0) { throw "WiX failed with exit code $LASTEXITCODE" }
Write-Output "Created $installer ($($files.Count) application files)"
