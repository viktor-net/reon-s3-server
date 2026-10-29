#Requires -Version 7.0
<#
.SYNOPSIS
    Заполняет dev-стенд демо-данными ИС РЕОН: объекты в S3 и строки PostgreSQL.

.DESCRIPTION
    Повторяет порядок записи сервиса: строка Pending -> объект в S3 с metadata
    reon-file-id / reon-sha256 -> строка Ready с ETag и версией объекта.
    Дополнительно создаёт бакеты административного фасада, квоты и журнал.

    Требования: запущенный стенд (docker compose), PowerShell 7+, контейнеры
    postgres и swift. Bash не нужен.

.PARAMETER Scope
    all (по умолчанию) — файловый домен и бакеты фасада;
    files — только files в Postgres и объекты в бакете приложения;
    admin — только бакеты фасада, их квоты и audit_events.

.EXAMPLE
    pwsh -File scripts/seed/seed.ps1

.EXAMPLE
    pwsh -File scripts/seed/seed.ps1 files

.NOTES
    Переменные окружения: SEED_COMPOSE_DIR, SEED_PG_SERVICE, SEED_SWIFT_SERVICE,
    SEED_PG_USER, SEED_PG_DB, SEED_BUCKET, SEED_KEY_PREFIX, SEED_OWNER,
    SEED_S3_ENDPOINT, SEED_S3_ACCESS_KEY, SEED_S3_SECRET_KEY, SEED_S3_REGION.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('all', 'files', 'admin')]
    [string]$Scope = 'all'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Ненулевой код возврата проверяем сами, а stderr дочерних процессов — это лог,
# а не ошибка PowerShell.
$PSNativeCommandUseErrorActionPreference = $false
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Resolve-Path (Join-Path $ScriptDir '../..')).Path

$PgService = if ($env:SEED_PG_SERVICE) { $env:SEED_PG_SERVICE } else { 'postgres' }
$SwiftService = if ($env:SEED_SWIFT_SERVICE) { $env:SEED_SWIFT_SERVICE } else { 'swift' }
$PgUser = if ($env:SEED_PG_USER) { $env:SEED_PG_USER } else { 'reon' }
$PgDb = if ($env:SEED_PG_DB) { $env:SEED_PG_DB } else { 'reon' }
$MainBucket = if ($env:SEED_BUCKET) { $env:SEED_BUCKET } else { 'reon-files' }
$KeyPrefix = if ($env:SEED_KEY_PREFIX) { $env:SEED_KEY_PREFIX } else { 'files/' }
$Owner = if ($env:SEED_OWNER) { $env:SEED_OWNER } else { 'system' }
$S3Endpoint = if ($env:SEED_S3_ENDPOINT) { $env:SEED_S3_ENDPOINT } else { 'http://127.0.0.1:8080' }
$S3AccessKey = if ($env:SEED_S3_ACCESS_KEY) { $env:SEED_S3_ACCESS_KEY } else { 'test:tester' }
$S3SecretKey = if ($env:SEED_S3_SECRET_KEY) { $env:SEED_S3_SECRET_KEY } else { 'testing' }
$S3Region = if ($env:SEED_S3_REGION) { $env:SEED_S3_REGION } else { 'us-east-1' }
$RemoteDir = '/tmp/reon-seed'

function Fail([string]$Message) {
    Write-Host "Ошибка: $Message" -ForegroundColor Red
    exit 1
}

function Test-StandCompose([string]$Path) {
    # Сервисы верхнего уровня: postgres и swift должны быть в блоке services.
    $inServices = $false
    $services = [System.Collections.Generic.List[string]]::new()

    foreach ($line in Get-Content -LiteralPath $Path) {
        if ($line -match '^[A-Za-z]') {
            $inServices = $line -match '^services:'
        }
        elseif ($inServices -and $line -match '^  ([A-Za-z0-9_.-]+):') {
            $services.Add($Matches[1])
        }
    }

    return (($services -contains 'postgres') -and ($services -contains 'swift'))
}

function Find-ComposeDir {
    if ($env:SEED_COMPOSE_DIR) {
        if (-not (Test-Path (Join-Path $env:SEED_COMPOSE_DIR 'docker-compose.yml'))) {
            Fail "в SEED_COMPOSE_DIR нет docker-compose.yml: $env:SEED_COMPOSE_DIR"
        }
        return (Resolve-Path $env:SEED_COMPOSE_DIR).Path
    }

    $candidates = @(
        $PWD.Path,
        $RepoRoot
    )

    foreach ($candidate in $candidates) {
        $file = Join-Path $candidate 'docker-compose.yml'
        if ((Test-Path $file) -and (Test-StandCompose $file)) {
            return (Resolve-Path $candidate).Path
        }
    }

    return $null
}

function Invoke-Compose([string[]]$Arguments) {
    Push-Location $ComposeDir
    try {
        & docker compose @Arguments
    }
    finally {
        Pop-Location
    }
}

function Invoke-Psql([string]$Sql) {
    Push-Location $ComposeDir
    try {
        $Sql | & docker compose exec -T $PgService `
            psql -v ON_ERROR_STOP=1 -q -U $PgUser -d $PgDb -f -
        if ($LASTEXITCODE -ne 0) {
            throw "psql завершился с кодом $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}

function Invoke-SeedPython {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [string]$InputText
    )

    Push-Location $ComposeDir
    try {
        $composeArguments = @(
            'exec', '-T',
            '-e', "SEED_BUCKET=$MainBucket",
            '-e', "SEED_KEY_PREFIX=$KeyPrefix",
            '-e', "SEED_OWNER=$Owner",
            '-e', "SEED_S3_ENDPOINT=$S3Endpoint",
            '-e', "SEED_S3_ACCESS_KEY=$S3AccessKey",
            '-e', "SEED_S3_SECRET_KEY=$S3SecretKey",
            '-e', "SEED_S3_REGION=$S3Region",
            $SwiftService, 'python3', "$RemoteDir/seed_data.py"
        ) + $Arguments

        if ($PSBoundParameters.ContainsKey('InputText')) {
            $result = $InputText | & docker compose @composeArguments
        }
        else {
            $result = & docker compose @composeArguments
        }

        if ($LASTEXITCODE -ne 0) {
            throw "python3 завершился с кодом $LASTEXITCODE"
        }

        return (($result | ForEach-Object { "$_" }) -join "`n")
    }
    finally {
        Pop-Location
    }
}

function Send-SeedPythonScript {
    $scriptText = Get-Content -Raw -LiteralPath (Join-Path $ScriptDir 'seed_data.py')
    Push-Location $ComposeDir
    try {
        $scriptText | & docker compose exec -T $SwiftService `
            sh -c "mkdir -p $RemoteDir && cat > $RemoteDir/seed_data.py"
        if ($LASTEXITCODE -ne 0) {
            throw "не удалось скопировать seed_data.py в контейнер $SwiftService"
        }
    }
    finally {
        Pop-Location
    }
}

function Invoke-SeedFiles {
    Write-Host ''
    Write-Host '== Файловый домен =='

    Write-Host '1/4 план: fileId, ключи, размер и SHA-256'
    $plan = Invoke-SeedPython -Arguments @('manifest')

    Write-Host '2/4 Postgres: строки files в статусе Pending'
    Invoke-Psql (Invoke-SeedPython -Arguments @('sql-pending') -InputText $plan)

    Write-Host '3/4 S3: объекты и metadata reon-file-id / reon-sha256'
    $uploaded = Invoke-SeedPython -Arguments @('upload', 'files') -InputText $plan

    Write-Host '4/4 Postgres: строки Ready с ETag и версией объекта'
    Invoke-Psql (Invoke-SeedPython -Arguments @('sql-ready') -InputText $uploaded)

    Write-Host '   Postgres: строки в остальных статусах жизненного цикла'
    Invoke-Psql (Invoke-SeedPython -Arguments @('sql-statuses'))
}

function Invoke-SeedAdmin {
    Write-Host ''
    Write-Host '== Бакеты административного фасада =='

    Write-Host '1/2 S3: бакеты, объекты и маркеры папок'
    $plan = Invoke-SeedPython -Arguments @('manifest')
    Invoke-SeedPython -Arguments @('upload', 'admin') -InputText $plan | Out-Null

    Write-Host '2/2 Postgres: квоты bucket_settings и журнал audit_events'
    Invoke-Psql (Invoke-SeedPython -Arguments @('sql-admin'))
}

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Fail 'Docker не найден в PATH.'
}

& docker compose version *> $null
if ($LASTEXITCODE -ne 0) {
    Fail 'Docker Compose v2 не найден.'
}

$ComposeDir = Find-ComposeDir
if (-not $ComposeDir) {
    Fail 'не найден docker-compose.yml стенда. Укажите каталог в SEED_COMPOSE_DIR.'
}

$running = (Invoke-Compose @('ps', '--services', '--status', 'running')) -split "`r?`n"
if ($running -notcontains $PgService) {
    Fail "сервис '$PgService' не запущен в $ComposeDir (docker compose up -d)."
}
if ($running -notcontains $SwiftService) {
    Fail "сервис '$SwiftService' не запущен в $ComposeDir (docker compose up -d)."
}

Write-Host "Стенд: $ComposeDir"
Write-Host "Бакет приложения: $MainBucket, префикс ключей: $KeyPrefix"

# Скрипт кладём в контейнер, чтобы stdin остался свободным для плана в JSON.
Send-SeedPythonScript

if ($Scope -in @('all', 'files')) {
    Invoke-SeedFiles
}

if ($Scope -in @('all', 'admin')) {
    Invoke-SeedAdmin
}

Write-Host ''
Write-Host 'Готово. Проверка:'
Write-Host "  curl 'http://localhost:8090/api/files?pageSize=5'"
Write-Host "  curl 'http://localhost:8090/api/admin/overview'"
Write-Host '  http://localhost:8090/scalar/api'
Write-Host ''
Write-Host 'Удалить демо-данные: pwsh -File scripts/seed/clean.ps1'
