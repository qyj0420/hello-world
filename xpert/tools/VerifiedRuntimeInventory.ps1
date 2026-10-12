function Assert-PublicModuleFiles {
    param([Parameter(Mandatory=$true)][string]$ModulesRoot)
    $root=[IO.Path]::GetFullPath($ModulesRoot).TrimEnd('\')
    $private=@('.cs','.c','.cpp','.cxx','.h','.hpp','.hxx','.obj','.lib','.exp','.pdb','.map','.ilk','.ipynb','.pyc','.pyo','.pem','.snk','.pfx','.key','.log','.tmp','.ps1','.cmd','.bat','.md')
    foreach($file in @(Get-ChildItem -LiteralPath $root -Recurse -File -Force)) {
        $relative=$file.FullName.Substring($root.Length+1)
        if($file.Extension -in $private -or ($file.Extension -eq '.py' -and $relative -cne 'Hybrid\hvx_offline_verify.py')) {
            throw ('Integrated payload contains private source, signing, debug or log files: '+$relative)
        }
    }
}

function Read-VerificationProofSnapshot {
    param([Parameter(Mandatory=$true)][string]$Path)
    $bytes=[IO.File]::ReadAllBytes($Path)
    $hash=[Security.Cryptography.SHA256]::Create()
    try{$sha=[BitConverter]::ToString($hash.ComputeHash($bytes)).Replace('-','')}finally{$hash.Dispose()}
    $value=[Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF) | ConvertFrom-Json
    [pscustomobject]@{Bytes=$bytes;SHA256=$sha;Value=$value}
}

function Assert-VerifiedRuntimeInventory {
    param([Parameter(Mandatory=$true)][string]$PayloadRoot,
          [Parameter(Mandatory=$true)][object[]]$Entries,
          [bool]$IncludeModules=$true,
          [bool]$StrictCandidateRoot=$false)
    $payload=[IO.Path]::GetFullPath($PayloadRoot).TrimEnd('\')
    $top=@('Xiongxin.OWTS.Desktop.exe','Xiongxin.OWTS.Desktop.exe.config','build-manifest.json')
    if($IncludeModules){$top+='module-manifest.json'}
    if($StrictCandidateRoot) {
        foreach($item in @(Get-ChildItem -LiteralPath $payload -Force)) {
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('Linked candidate root path is prohibited: '+$item.FullName)}
            if($item.PSIsContainer) {
                if($item.Name -notin @('assets','modules','审核证据','验证证据')){throw ('Unexpected candidate root directory: '+$item.Name)}
            } elseif($item.Name -notin $top -and $item.Name -notin @('使用说明.md','修正汇总.md','审核汇总.md','验证记录.md','一体化接入说明.md')) {
                throw ('Unexpected candidate root file: '+$item.Name)
            }
        }
    }
    # Hidden/system entries remain runtime entries; omitting them would make an
    # apparently closed inventory ignore injected files or linked directories.
    $files=@($top | ForEach-Object {Get-Item -LiteralPath (Join-Path $payload $_) -Force -ErrorAction Stop})+
        @(Get-ChildItem -LiteralPath (Join-Path $payload 'assets') -Recurse -File -Force -ErrorAction Stop)
    if($IncludeModules){$files+=@(Get-ChildItem -LiteralPath (Join-Path $payload 'modules') -Recurse -File -Force -ErrorAction Stop)}
    # Enumerate and reject links before trusting either source or copied payload.
    $directories=@(Get-Item -LiteralPath $payload -Force)+@(Get-ChildItem -LiteralPath (Join-Path $payload 'assets') -Recurse -Directory -Force)
    if($IncludeModules){$directories+=@(Get-Item -LiteralPath (Join-Path $payload 'modules') -Force)+@(Get-ChildItem -LiteralPath (Join-Path $payload 'modules') -Recurse -Directory -Force)}
    $directories+=Get-Item -LiteralPath (Join-Path $payload 'assets') -Force
    foreach($item in @($files)+@($directories)) {
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw ('Linked runtime path is prohibited: '+$item.FullName)}
    }
    $expected=@($Entries | Where-Object {$IncludeModules -or ([string]$_.Path -ne 'module-manifest.json' -and -not ([string]$_.Path).Replace('/','\').StartsWith('modules\',[StringComparison]::OrdinalIgnoreCase))})
    if($files.Count -ne $expected.Count){throw 'The copied runtime inventory is not exhaustive.'}
    $actual=@{}
    foreach($file in $files){$actual[$file.FullName.Substring($payload.Length+1).Replace('/','\')]=$file.FullName}
    $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $expected) {
        $relative=([string]$entry.Path).Replace('/','\')
        if(-not $seen.Add($relative) -or -not $actual.ContainsKey($relative) -or $entry.SHA256 -notmatch '^[A-Fa-f0-9]{64}$'){throw ('Invalid verified runtime path: '+$relative)}
        if((Get-FileHash -LiteralPath $actual[$relative] -Algorithm SHA256).Hash -ne $entry.SHA256){throw ('Verified runtime file changed: '+$relative)}
    }
    return $files.Count
}
