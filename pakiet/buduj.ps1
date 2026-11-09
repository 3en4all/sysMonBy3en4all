# Buduje pakiet MSIX do wysłania w Partner Center (bez podpisu — Store podpisuje sam).
# Użycie: powershell -File pakiet\buduj.ps1   → pakiet\sysMonBy3en4all-<wersja>.msix
$ErrorActionPreference = 'Stop'
$proj = Split-Path $PSScriptRoot
$sdk = (Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\10.*\x64\makeappx.exe' | Sort-Object FullName | Select-Object -Last 1).DirectoryName
$uklad = Join-Path $PSScriptRoot 'uklad'
if (Test-Path $uklad) { Remove-Item $uklad -Recurse -Force }

dotnet publish "$proj\sysMonBy3en4all.csproj" -c Release -o $uklad
if ($LASTEXITCODE) { throw 'dotnet publish nie przeszedł' }
Get-ChildItem $uklad -Filter *.pdb | Remove-Item

Copy-Item "$PSScriptRoot\Package.appxmanifest" "$uklad\AppxManifest.xml"
Copy-Item "$PSScriptRoot\THIRD-PARTY-NOTICES.txt" $uklad
New-Item -ItemType Directory "$uklad\Assets" | Out-Null
Get-ChildItem "$proj\Assets\*.png" -Exclude 'sklep-*' | Copy-Item -Destination "$uklad\Assets"

# resources.pri: wybór wariantów ikon (scale-200, targetsize) przez Windows
Push-Location $uklad
& "$sdk\makepri.exe" createconfig /cf priconfig.xml /dq pl-PL /o | Out-Null
& "$sdk\makepri.exe" new /pr . /cf priconfig.xml /mn AppxManifest.xml /of resources.pri /o | Out-Null
if ($LASTEXITCODE) { Pop-Location; throw 'makepri nie przeszedł' }
Remove-Item priconfig.xml
Pop-Location

$wersja = ([xml](Get-Content "$uklad\AppxManifest.xml")).Package.Identity.Version
$msix = Join-Path $PSScriptRoot "sysMonBy3en4all-$wersja.msix"
& "$sdk\makeappx.exe" pack /d $uklad /p $msix /o
if ($LASTEXITCODE) { throw 'makeappx nie przeszedł' }
Write-Host "Gotowe: $msix"
