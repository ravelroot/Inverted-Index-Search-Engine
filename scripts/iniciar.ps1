$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

function New-Secret([int] $Length = 40) {
    $chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'.ToCharArray()
    $bytes = New-Object byte[] $Length
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

$envFile = Join-Path (Get-Location) '.env'
if (-not (Test-Path $envFile)) {
    Copy-Item '.env.example' $envFile
    Write-Host 'Arquivo .env criado a partir do .env.example.'
}

$lines = Get-Content $envFile | ForEach-Object {
    if ($_ -eq 'SONDA_API_KEY=') { "SONDA_API_KEY=$(New-Secret 40)" } else { $_ }
}
[System.IO.File]::WriteAllLines($envFile, $lines, (New-Object System.Text.UTF8Encoding $false))

$values = @{}
foreach ($line in $lines) {
    if ($line -match '^([A-Z_]+)=(.*)$') {
        $values[$Matches[1]] = $Matches[2]
    }
}
$port = if ($values['SONDA_PORT']) { $values['SONDA_PORT'] } else { '8080' }
$key = $values['SONDA_API_KEY']

function Wait-Ready {
    for ($i = 0; $i -lt 90; $i++) {
        try {
            Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 "http://127.0.0.1:$port/health" | Out-Null
            return $true
        }
        catch {
            Start-Sleep -Seconds 2
        }
    }
    return $false
}

$sdk = $null
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $sdk = dotnet --list-sdks 2>$null | Where-Object { $_ -match '^10\.' } | Select-Object -First 1
}

if ($sdk) {
    Write-Host 'Compilando com o .NET SDK instalado...'
    dotnet build src/Sonda.Server -c Release -nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }

    $env:Urls = "http://127.0.0.1:$port"
    $env:Sonda__Api__ApiKey = $key
    $server = Start-Process dotnet -ArgumentList 'src/Sonda.Server/bin/Release/net10.0/Sonda.Server.dll' -NoNewWindow -PassThru
}
elseif (Get-Command docker -ErrorAction SilentlyContinue) {
    docker compose up -d --build
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    $server = $null
}
else {
    Write-Host 'Instale o .NET SDK 10 (https://dot.net) ou o Docker Desktop e rode de novo.' -ForegroundColor Yellow
    exit 1
}

Write-Host -NoNewline 'Aguardando o servidor'
if (-not (Wait-Ready)) {
    Write-Host ''
    Write-Host 'O servidor não respondeu em 3 minutos.' -ForegroundColor Yellow
    exit 1
}
Write-Host ''
Write-Host "Painel:          http://localhost:$port"
Write-Host "Chave de acesso: $key"
Start-Process "http://localhost:$port"

if ($server) {
    Write-Host 'Feche esta janela ou aperte Ctrl+C para parar o servidor.'
    Wait-Process -Id $server.Id
}
