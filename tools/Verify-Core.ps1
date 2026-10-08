param([string]$AssemblyPath=(Join-Path $PSScriptRoot '..\bin\2023\Release\MEP_Check_Clash_ver1.dll'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Runtime.Serialization,System.IO.Compression,System.IO.Compression.FileSystem,System.Xml.Linq
[Reflection.Assembly]::LoadFrom('C:\Program Files\Autodesk\Revit 2023\RevitAPI.dll') | Out-Null
[Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath)) | Out-Null
$categoryIds=[MEP_Check_Clash_ver1.ClashEngine]::CategoryIds([MEP_Check_Clash_ver1.ClashEngine]::MepCategories)
if (!$categoryIds.Contains([long][Autodesk.Revit.DB.BuiltInCategory]::OST_CableTray) -or !$categoryIds.Contains([long][Autodesk.Revit.DB.BuiltInCategory]::OST_CableTrayFitting)) { throw 'Cable Tray or fittings missing from MEP preset.' }
$assembly=[Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath))
if ($assembly.GetManifestResourceNames() -notcontains 'ClashSolution.Logo') { throw 'Tool logo is not embedded in the DLL.' }
$folder=Join-Path $PSScriptRoot '..\artifacts\verification'
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$path=Join-Path $folder 'profiles.json'
$fixture='{"schema":1,"profiles":[{"name":"MEP – Kết cấu","source_a_key":"HOST","source_b_key":"LINK:sample","categories_a":[{"id":-2008044,"name":"Pipes"}],"categories_b":[],"scope":"levels","levels":[{"id":42,"name":"Level 02"}],"include_nonvisible":false,"show_system_details":true,"filters_a":[{"parameter":"System Type","operator":"Contains","value":"CHW"}],"filter_mode_a":"any"}]}'
[IO.File]::WriteAllText($path,$fixture,[Text.UTF8Encoding]::new($false))
$profiles=[MEP_Check_Clash_ver1.ProfileStore]::Load($path)
if ($profiles.Count -ne 1 -or $profiles[0].FiltersA[0].Value -ne 'CHW' -or $profiles[0].CategoriesA[0].Id -ne -2008044) { throw 'Python profile import failed.' }
[MEP_Check_Clash_ver1.ProfileStore]::Save($profiles,$path)
$reloaded=[MEP_Check_Clash_ver1.ProfileStore]::Load($path)
if ($reloaded[0].Name -ne $profiles[0].Name -or $reloaded[0].Levels[0].Id -ne 42 -or !(Test-Path -LiteralPath ($path+'.bak'))) { throw 'Profile roundtrip or backup failed.' }
$bare=Join-Path $folder 'legacy-profiles.json'
[IO.File]::WriteAllText($bare,'[{"name":"Legacy","filter_a":{"parameter":"Service","value":"CHW","operator":"Equals"}}]')
$legacy=[MEP_Check_Clash_ver1.ProfileStore]::Load($bare)
if (!$legacy[0].ShowSystemDetails -or $legacy[0].LegacyFilterA.Value -ne 'CHW') { throw 'Legacy profile compatibility failed.' }
if ($profiles[0].Criteria.MinVolumeCm3 -ne 0.10 -or $legacy[0].Criteria.MinVolumeCm3 -ne 0.10) { throw 'Legacy clash criteria defaults failed.' }
$criteria=[MEP_Check_Clash_ver1.ClashCriteria]@{MinVolumeCm3=2.5;MinIntersectionBoxMm=5;IgnoreConnectedMep=$true}
if ($criteria.Accepts(2.5,10,10,10) -or $criteria.Accepts(3,10,4,10) -or !$criteria.Accepts(3,10,5,10)) { throw 'Volume / intersection box thresholds failed.' }
$criteria.MinVolumeCm3=0
$criteria.MinIntersectionBoxMm=0
if ($criteria.Accepts(0,100,100,100) -or !$criteria.Accepts(0.01,0.1,0.1,0.1)) { throw 'Zero-volume contact / disabled box threshold failed.' }
$criteria.MinVolumeCm3=-1
$invalid=$false
try { $criteria.Validate() } catch { $invalid=$true }
if (!$invalid) { throw 'Negative threshold was accepted.' }
$criteria.MinVolumeCm3=[double]::NaN
$invalid=$false
try { $criteria.Validate() } catch { $invalid=$true }
if (!$invalid) { throw 'NaN threshold was accepted.' }
$profiles[0].Criteria=[MEP_Check_Clash_ver1.ClashCriteria]@{MinVolumeCm3=3.75;MinIntersectionBoxMm=8;IgnoreConnectedMep=$true}
$profiles[0].Scope='active_view_level'
$portable=Join-Path $folder 'portable-profile.json'
[MEP_Check_Clash_ver1.ProfileStore]::Save($profiles,$portable)
$portableProfiles=[MEP_Check_Clash_ver1.ProfileStore]::Load($portable)
if ($portableProfiles[0].Scope -ne 'active_view_level' -or $portableProfiles[0].Criteria.MinVolumeCm3 -ne 3.75 -or $portableProfiles[0].Criteria.MinIntersectionBoxMm -ne 8 -or !$portableProfiles[0].Criteria.IgnoreConnectedMep -or $portableProfiles[0].FiltersA[0].Value -ne 'CHW') { throw 'Portable profile did not preserve scope, filters and criteria.' }
$bad=Join-Path $folder 'invalid-profile.json'
[IO.File]::WriteAllText($bad,'{ invalid')
$failed=$false
try { [MEP_Check_Clash_ver1.ProfileStore]::Load($bad) | Out-Null } catch { $failed=$true }
if (!$failed -or [IO.File]::ReadAllText($bad) -ne '{ invalid') { throw 'Malformed profile was not preserved.' }
$values=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
$values.Add('System Type',' CHW Supply ')
$rules=New-Object 'System.Collections.Generic.List[MEP_Check_Clash_ver1.FilterRule]'
$rules.Add([MEP_Check_Clash_ver1.FilterRule]@{Parameter='system type';Operator='Contains';Value='chw'})
$rules.Add([MEP_Check_Clash_ver1.FilterRule]@{Parameter='Missing';Operator='Not Equals';Value='x'})
if ([MEP_Check_Clash_ver1.ClashEngine]::Matches($values,$rules,$false)) { throw 'AND matched a missing parameter.' }
if (![MEP_Check_Clash_ver1.ClashEngine]::Matches($values,$rules,$true)) { throw 'OR / case-insensitive matching failed.' }
$headers=[string[]]@('Clash Key','Element ID A','Category A','Type A')
$rows=New-Object 'System.Collections.Generic.List[string[]]'
$rows.Add([string[]]@('HOST|a::LINK|b','2147483650','Ống, "CHW"',('Line 1'+[Environment]::NewLine+'Line 2')))
$rows.Add([string[]]@('=HYPERLINK("bad")','42','<Pipes & Ducts>','@SUM(1)'))
[MEP_Check_Clash_ver1.ResultExporter]::Csv((Join-Path $folder 'results.csv'),$headers,$rows)
[MEP_Check_Clash_ver1.ResultExporter]::Xlsx((Join-Path $folder 'results.xlsx'),$headers,$rows)
$archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $folder 'results.xlsx'))
try {
    foreach ($entry in $archive.Entries) { $reader=[IO.StreamReader]::new($entry.Open()); try { [xml]$content=$reader.ReadToEnd() } finally { $reader.Dispose() } }
    if ($archive.Entries.Count -ne 6) { throw 'Unexpected workbook package structure.' }
} finally { $archive.Dispose() }
Write-Output 'PASS: Cable Tray preset and embedded logo; Python/legacy and portable JSON profiles, defaults, scope/filters/criteria roundtrip, volume/box/contact threshold boundaries, invalid criteria, backup and malformed-file preservation, AND/OR filters, CSV and XLSX XML.'
Write-Output "Verification files: $folder"
