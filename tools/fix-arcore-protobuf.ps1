<#
.SYNOPSIS
    Replaces ARCore's broken Google.Protobuf.dll with Unity's, so that
    EditorApplication.isCompiling stops throwing TypeLoadException.

.DESCRIPTION
    ARCore Extensions ships Editor/Scripts/Internal/Analytics/Google.Protobuf.dll at version
    0.0.0.0, unsigned, and missing Google.Protobuf.IBufferMessage. Unity's own
    MsBuildCompilation typerefs Google.Protobuf 3.23.0.0, PublicKeyToken=a7d26565bac4d604.
    Same assembly SIMPLE NAME, so ARCore's copy wins the Editor domain and every call to
    EditorApplication.isCompiling throws:

        TypeLoadException: Could not load type of field
        'UnityEditor.Scripting.ScriptCompilation.MsBuild.MsBuildCompilation:_currentBuildTask'

    The visible symptom is that exception at every domain reload, thrown from
    URP's ScriptableRendererData.OnValidate. That one is mostly cosmetic -- SetDirty() has
    already run by then; what is skipped is null-renderer-feature validation and a hide-flags
    migration. The real cost is that ANY editor tooling touching compilation state breaks,
    which is why CoplayDev's unity-mcp could never start here.

    ARCore's four Editor analytics files compile against the DLL, so it cannot be deleted.
    Overwriting it with Unity's real 3.23 assembly makes both references resolve to the same
    correct type.

    NOT PERMANENT: Library/PackageCache is regenerated whenever the package re-resolves, and
    the fix is lost with it. That is why this is a script and not a one-off command. Re-run it
    after any package resolve, Unity version change, or Library wipe.

.PARAMETER Restore
    Puts ARCore's original DLL back from the .orig backup.

.EXAMPLE
    ./tools/fix-arcore-protobuf.ps1
    ./tools/fix-arcore-protobuf.ps1 -Restore
#>
[CmdletBinding()]
param(
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot

# The Editor holds the DLL loaded in its Mono domain: the copy would fail on a file lock, and
# even if it succeeded the running domain would keep the poisoned type until a restart. Refuse
# rather than half-apply.
if (Test-Path (Join-Path $repo 'Temp/UnityLockfile')) {
    Write-Host 'Unity is OPEN. Close it and run this again.' -ForegroundColor Red
    Write-Host 'The DLL is loaded into the running Editor domain; it cannot be replaced underneath it.'
    exit 1
}

$target = Get-ChildItem -Path (Join-Path $repo 'Library/PackageCache') -Recurse -Filter 'Google.Protobuf.dll' -ErrorAction SilentlyContinue |
          Where-Object { $_.FullName -like '*arcore*' } |
          Select-Object -First 1

if ($null -eq $target) {
    Write-Host 'No ARCore Google.Protobuf.dll found under Library/PackageCache.' -ForegroundColor Yellow
    Write-Host 'Either the package is not resolved yet (open Unity once), or the fault is already gone.'
    exit 1
}

$backup = "$($target.FullName).orig"

if ($Restore) {
    if (-not (Test-Path $backup)) {
        Write-Host "No backup at $backup -- nothing to restore." -ForegroundColor Yellow
        exit 1
    }
    Copy-Item $backup $target.FullName -Force
    Remove-Item $backup -Force
    Write-Host "Restored ARCore's original Google.Protobuf.dll." -ForegroundColor Green
    exit 0
}

$unity = 'C:/Program Files/Unity/Hub/Editor/6000.5.7f1/Editor/Data/Managed/Google.Protobuf.dll'
if (-not (Test-Path $unity)) {
    Write-Host "Unity's Google.Protobuf.dll not found at:" -ForegroundColor Red
    Write-Host "  $unity"
    Write-Host 'Point $unity at the matching path for your Editor version.'
    exit 1
}

# Only back up once. Running this twice must not overwrite the good backup with the already
# patched file -- that would destroy the only copy of the original.
if (-not (Test-Path $backup)) {
    Copy-Item $target.FullName $backup
    Write-Host "Backed up   : $backup"
} else {
    Write-Host "Backup exists: $backup (left alone)"
}

Copy-Item $unity $target.FullName -Force

Write-Host "Patched     : $($target.FullName)" -ForegroundColor Green
Write-Host ''
Write-Host 'Now reopen Unity. The TypeLoadException should be gone from the Console at startup.'
Write-Host 'Re-run this after any package re-resolve -- Library/PackageCache does not keep the fix.'
