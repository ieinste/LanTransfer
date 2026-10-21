$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$destination = Join-Path $root 'tools\net40'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$url = 'https://api.nuget.org/v3-flatcontainer/microsoft.netframework.referenceassemblies.net40/1.0.3/microsoft.netframework.referenceassemblies.net40.1.0.3.nupkg'
$package = Join-Path $destination 'referenceassemblies.zip'
Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $package
Expand-Archive -LiteralPath $package -DestinationPath (Join-Path $destination 'reference') -Force
Write-Host 'Microsoft .NET Framework 4.0 build reference assemblies are ready.'
