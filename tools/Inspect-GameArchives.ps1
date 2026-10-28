param([Parameter(Mandatory=$true)][string]$Game)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($Game)
$files=@(Get-ChildItem -LiteralPath (Join-Path $root 'mp') -Filter 'mp_*.db' -File | Select-Object -First 128)
$files+=Get-Item -LiteralPath (Join-Path $root 'patches/xrmpe_configs.db')
$results=foreach($file in $files) {
    if(($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Linked archive rejected'}
    $stream=[IO.File]::OpenRead($file.FullName)
    try {
        $chunks=@(); $fields=@{}
        for($i=0;$i -lt 32 -and $stream.Position -lt $stream.Length;$i++) {
            if($stream.Length-$stream.Position -lt 8){throw 'Truncated chunk header'}
            $h=[byte[]]::new(8); $stream.ReadExactly($h)
            $id=[BitConverter]::ToUInt32($h,0); $length=[BitConverter]::ToUInt32($h,4)
            if($length -gt $stream.Length-$stream.Position){throw 'Truncated chunk'}
            $chunks+=@{Id=$id;Size=$length;Compressed=(($id -band 0x80000000L) -ne 0)}
            if($id -eq 666 -and $length -le 4096) {
                $bytes=[byte[]]::new($length); $stream.ReadExactly($bytes)
                foreach($line in ([Text.Encoding]::UTF8.GetString($bytes) -split "`n")) {
                    if($line -match '^\s*(auto_load|entry_point|level_name|level_ver)\s*=\s*(.*)$'){$fields[$Matches[1]]=$Matches[2].Trim()}
                }
            } else {$stream.Position+=$length}
        }
        [pscustomobject]@{Archive=$file.Name;Bytes=$file.Length;Header=$fields;Chunks=$chunks;InternalConfigurationDecoded=$false}
    } finally {$stream.Dispose()}
}
$results | ConvertTo-Json -Depth 6