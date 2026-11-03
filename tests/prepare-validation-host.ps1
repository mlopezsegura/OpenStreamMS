param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path $PSScriptRoot -Parent
$taskOut = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $taskOut | Out-Null
$taskProgram = Get-Content -LiteralPath (Join-Path $taskRepo 'Program.cs') -Raw
$taskPublicMethod = [regex]::Match($taskProgram, '(?s)static bool IsPublicPath\(string path\) =>.*?;').Value
if (-not $taskPublicMethod) { throw 'IsPublicPath not found in Program.cs' }
$taskTemplate = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ValidationHost.cs.txt') -Raw
[IO.File]::WriteAllText((Join-Path $taskOut 'Program.cs'),$taskTemplate.Replace('// IS_PUBLIC_PATH_FROM_PROGRAM_CS',$taskPublicMethod))
$taskTrayPath = [System.Security.SecurityElement]::Escape((Join-Path $taskRepo 'Services/TrayApp/TrayText.cs'))
$taskTrayLocales = [System.Security.SecurityElement]::Escape((Join-Path $taskRepo 'Services/TrayApp/Locales/*.json'))
$taskProject = @"
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><Compile Include="$taskTrayPath" Link="TrayText.cs" /></ItemGroup>
  <ItemGroup><EmbeddedResource Include="$taskTrayLocales" LogicalName="OpenStreamMS.TrayLocales.%(Filename).json" /></ItemGroup>
</Project>
"@
[IO.File]::WriteAllText((Join-Path $taskOut 'ValidationHost.csproj'),$taskProject)
Write-Output "Validation host generated at $taskOut using the current IsPublicPath and TrayText source."
