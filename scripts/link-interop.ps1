# Links <repo>/interop to <game>/BepInEx/interop so the projects can reference the game's interop DLLs.
# Usage:  .\scripts\link-interop.ps1 "C:\Program Files (x86)\Steam\steamapps\common\Dice Kingdoms"
# (A directory junction is used: no administrator rights needed.)
param([Parameter(Mandatory = $true)][string]$GameDir)

$target = Join-Path $GameDir "BepInEx\interop"
if (-not (Test-Path $target)) {
    throw "No BepInEx\interop folder in '$GameDir'. Install BepInEx 6 IL2CPP and run the game once first."
}
$link = Join-Path (Split-Path $PSScriptRoot -Parent) "interop"
# "rmdir" removes only the junction itself. (Remove-Item -Recurse on a junction can wipe the TARGET folder in Windows PowerShell 5.1.)
if (Test-Path $link) { cmd /c rmdir "$link" | Out-Null }
New-Item -ItemType Junction -Path $link -Target $target | Out-Null
Write-Host "interop -> $target"
