<#
  Pulse Elite Companion - instalador do driver WinUSB (MI_03 do dongle PS Link).

  Faz, com ferramentas 100% nativas do Windows (sem WDK):
    1. gera um certificado auto-assinado de assinatura de codigo;
    2. cria e assina o catalogo (.cat) do PulseElite.inf;
    3. confia o certificado (LocalMachine\Root + TrustedPublisher);
    4. instala o driver (pnputil) -> MI_03 passa a usar winusb.sys com o nosso GUID fixo.

  ATENCAO: o passo 3 coloca um certificado auto-assinado na Raiz Confiavel da maquina.
  E a mesma concessao de confianca que o Zadig faz. Reversivel: rode uninstall.ps1.

  Uso: clique direito > "Executar com o PowerShell", ou:  powershell -ExecutionPolicy Bypass -File install.ps1
#>
param([switch]$Elevated)
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$inf  = Join-Path $here "PulseElite.inf"
$cat  = Join-Path $here "PulseElite.cat"
$cer  = Join-Path $here "PulseElite.cer"
$subject = "CN=Pulse Elite Companion (self-signed driver cert)"

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Write-Host "Elevando (UAC)..."
    Start-Process powershell -Verb RunAs -Wait -ArgumentList `
        "-NoProfile","-ExecutionPolicy","Bypass","-File","`"$($MyInvocation.MyCommand.Path)`"","-Elevated"
    return
}

Write-Host "[1/5] Limpando certificados antigos e gerando um novo (auto-assinado)..."
foreach ($store in "My","Root","TrustedPublisher") {
    Get-ChildItem "Cert:\LocalMachine\$store" -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $subject } |
        ForEach-Object { Remove-Item $_.PSPath -Force -ErrorAction SilentlyContinue }
}
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
    -CertStoreLocation Cert:\LocalMachine\My -NotAfter (Get-Date).AddYears(10)

Write-Host "[2/5] Confiando o certificado (Root + TrustedPublisher) ANTES de assinar..."
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null

Write-Host "[3/5] Criando e assinando o catalogo do driver..."
if (Test-Path $cat) { Remove-Item $cat -Force }
New-FileCatalog -Path $inf -CatalogFilePath $cat -CatalogVersion 2 | Out-Null
$sig = Set-AuthenticodeSignature -FilePath $cat -Certificate $cert
Write-Host "  status da assinatura: $($sig.Status)"
if ($sig.Status -ne "Valid") { Write-Warning "assinatura nao 'Valid' ($($sig.Status)); tentando instalar mesmo assim..." }

Write-Host "[4/5] Instalando o driver..."
pnputil /add-driver "$inf" /install
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 259 -and $LASTEXITCODE -ne 3010) {
    Write-Warning "pnputil retornou codigo $LASTEXITCODE"
}

Write-Host "[5/5] Verificando bind do MI_03..."
$svc = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -match "VID_054C&PID_0ECC&MI_03($|\\)" } |
    ForEach-Object { (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_Device_Service -ErrorAction SilentlyContinue).Data }
Write-Host "MI_03 service = $svc"
if ($svc -eq "WinUSB") {
    Write-Host "`nOK! MI_03 esta no WinUSB. Pode iniciar o Pulse Elite Companion." -ForegroundColor Green
} else {
    Write-Warning "MI_03 nao ficou em WinUSB (svc=$svc). Se o dongle nao estiver plugado, plugue e rode de novo."
}
if ($Elevated) { Write-Host "`n(pressione Enter para fechar)"; [void](Read-Host) }
