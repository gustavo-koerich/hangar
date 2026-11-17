<#
  Compila o Hangar como um .exe autocontido em .\app\ e cria o atalho na Área de Trabalho.
  Uso: botão direito → Executar com o PowerShell (ou .\publicar.ps1 no terminal).
  No PC novo: se a pasta app\ veio junto na cópia, basta rodar com -SoAtalho (não precisa do .NET SDK).
#>
param([switch]$SoAtalho)
$ErrorActionPreference = 'Stop'
$raiz = $PSScriptRoot
$app = Join-Path $raiz 'app'
$exe = Join-Path $app 'Hangar.exe'

if (-not $SoAtalho) {
  # O SDK deste PC fica instalado só para o usuário (igual ao RadialShift).
  $dotnetUsuario = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
  if (Test-Path (Join-Path $dotnetUsuario 'dotnet.exe')) { $env:DOTNET_ROOT = $dotnetUsuario; $env:PATH = "$dotnetUsuario;$env:PATH" }
  $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
  if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Falta o .NET SDK 8: winget install Microsoft.DotNet.SDK.8' }

  if (Get-Process Hangar -ErrorAction SilentlyContinue) { throw 'Feche o Hangar antes de publicar.' }
  if (Test-Path $app) { Remove-Item $app -Recurse -Force }
  dotnet publish (Join-Path $raiz 'src\Hangar\Hangar.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $app --nologo -v q
  if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar.' }
}

if (-not (Test-Path $exe)) { throw "Não achei $exe. Rode sem -SoAtalho para compilar." }
$atalho = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Hangar.lnk'
$s = (New-Object -ComObject WScript.Shell).CreateShortcut($atalho)
$s.TargetPath = $exe
$s.WorkingDirectory = $app
$s.IconLocation = "$exe,0"
$s.Description = 'Hangar — painel dos projetos'
$s.Save()
Write-Host "Pronto: $exe`nAtalho: $atalho"
