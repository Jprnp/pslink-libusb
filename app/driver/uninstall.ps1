<#
  Pulse Elite Companion - desinstala o driver WinUSB e desfaz a confianca do certificado.
  Reverte o MI_03 pro driver in-box (HidUsb). Uso: clique direito > Executar com o PowerShell.
#>
param([switch]$Elevated)
$ErrorActionPreference = "Continue"
$subject = "CN=Pulse Elite Companion (self-signed driver cert)"

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if (-not (Test-Admin)) {
    Start-Process powershell -Verb RunAs -ArgumentList `
        "-NoProfile","-ExecutionPolicy","Bypass","-File","`"$($MyInvocation.MyCommand.Path)`"","-Elevated"
    return
}

Write-Host "[1/2] Removendo o pacote de driver do store..."
$current = $null; $targets = @()
foreach ($l in (pnputil /enum-drivers)) {
    if ($l -match '(oem\d+\.inf)') { $current = $Matches[1] }
    if ($l -match 'PulseElite\.inf' -and $current) { $targets += $current }
}
$targets = $targets | Select-Object -Unique
if ($targets) {
    foreach ($t in $targets) {
        Write-Host "  removendo $t ..."
        pnputil /delete-driver $t /uninstall /force
    }
} else {
    Write-Host "  (nenhum pacote PulseElite.inf encontrado no store)"
}

Write-Host "[2/2] Removendo o certificado auto-assinado das lojas..."
foreach ($store in "Root","TrustedPublisher","My") {
    Get-ChildItem "Cert:\LocalMachine\$store" -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $subject } |
        ForEach-Object { Write-Host "  removendo cert de $store"; Remove-Item $_.PSPath -Force -ErrorAction SilentlyContinue }
}

$svc = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -match "VID_054C&PID_0ECC&MI_03($|\\)" } |
    ForEach-Object { (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_Device_Service -ErrorAction SilentlyContinue).Data }
Write-Host "`nMI_03 service agora = $svc (esperado: HidUsb, ou vazio ate replugar)"
if ($Elevated) { Write-Host "`n(pressione Enter para fechar)"; [void](Read-Host) }
