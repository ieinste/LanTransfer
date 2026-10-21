param([switch]$Test, [switch]$LargeTest, [switch]$UiTest, [string]$OutputDirectory = 'release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Please install .NET Framework 4.x.' }
$referenceDir = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.0'
if (!(Test-Path -LiteralPath (Join-Path $referenceDir 'mscorlib.dll'))) {
    $referenceDir = Join-Path $root 'tools\net40\reference\build\.NETFramework\v4.0'
}
if (!(Test-Path -LiteralPath (Join-Path $referenceDir 'mscorlib.dll'))) {
    throw '.NET Framework 4.0 reference assemblies are required to verify Windows 7 API compatibility.'
}
$references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll') | ForEach-Object { '/reference:' + (Join-Path $referenceDir $_) }
$outDir = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $root $OutputDirectory }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler /nologo /noconfig /nostdlib+ /optimize+ /platform:anycpu /target:winexe "/out:$outDir\LanTransfer.exe" $references $sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
Copy-Item -LiteralPath (Join-Path $root 'src\LanTransfer.exe.config') -Destination (Join-Path $outDir 'LanTransfer.exe.config')
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $outDir
$docDir = Join-Path $outDir 'docs'
New-Item -ItemType Directory -Force -Path $docDir | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'docs\PLATFORM-SUPPORT.md') -Destination $docDir
if ($Test -or $LargeTest) {
    & $compiler /nologo /noconfig /nostdlib+ /optimize+ /platform:anycpu /target:exe "/out:$outDir\TransferTests.exe" $references (Join-Path $root 'src\TransferCore.cs') (Join-Path $root 'tests\Program.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    if ($LargeTest) { & (Join-Path $outDir 'TransferTests.exe') --large }
    else { & (Join-Path $outDir 'TransferTests.exe') }
    if ($LASTEXITCODE -ne 0) { throw 'Transfer tests failed.' }
}
if ($Test -or $LargeTest -or $UiTest) {
    & $compiler /nologo /noconfig /nostdlib+ /optimize+ /platform:anycpu /target:exe /main:UiSmoke "/out:$outDir\UiSmoke.exe" $references $sources (Join-Path $root 'tests\UiSmoke.cs')
    if ($LASTEXITCODE -ne 0) { throw 'UI check compilation failed.' }
    & (Join-Path $outDir 'UiSmoke.exe') (Join-Path $root 'test-output\ui-v0.2.1')
    if ($LASTEXITCODE -ne 0) { throw 'UI checks failed.' }
}
Write-Host "Built: $outDir\LanTransfer.exe"
