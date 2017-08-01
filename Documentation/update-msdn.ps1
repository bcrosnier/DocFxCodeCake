$ErrorActionPreference = "Stop"

# Downloads MSDN documentation xref files, and puts them in msdn/.

$MsdnNupkgUrl = 'https://www.nuget.org/api/v2/package/msdn.4.5.2/0.1.0-alpha-1611021200'
$MsdnNupkgFilePath = [System.IO.Path]::Combine( $PSScriptRoot, 'msdn\msdn.4.5.2.zip')
$OutPath = [System.IO.Path]::Combine( $PSScriptRoot, 'msdn\msdn.4.5.2')

If(!(Test-Path $OutPath)){
    New-Item -ItemType Directory -Force -Path $OutPath | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    (New-Object Net.WebClient).DownloadFile($MsdnNupkgUrl, $MsdnNupkgFilePath);
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($MsdnNupkgFilePath, $OutPath)
}
