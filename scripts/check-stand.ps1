# Проверяет, что стенд можно поднять на этой машине, и готовит .env.
#
# Что проверяет:
#   1. docker и docker compose v2 доступны, демон запущен;
#   2. .env есть (создаёт из .env.example, если файла нет);
#   3. на месте файлы, которые монтируются в контейнеры;
#   4. образ Swift S3: если его нет — предупреждает, что он соберётся
#      при первом `docker compose up -d --build`;
#   5. порты стенда не заняты чужими процессами (если стенд ещё не поднят).
#
# Запуск:
#   pwsh -File scripts/check-stand.ps1
#
# Код возврата: 0 — можно запускать, 1 — есть блокирующая проблема.

# Windows PowerShell 5.1 бросает NativeCommandError при `2>$null` у внешних
# команд, поэтому ошибки разбираем сами: по $LASTEXITCODE и Test-Path.
$ErrorActionPreference = 'Continue'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Split-Path -Parent $ScriptDir
Set-Location $RepoRoot

$failed = $false
$warnings = 0

function Ok([string]$message) { Write-Host "  [ok]   $message" -ForegroundColor Green }
function Warn([string]$message) {
    Write-Host "  [warn] $message" -ForegroundColor Yellow
    $script:warnings++
}
function Fail([string]$message) {
    Write-Host "  [fail] $message" -ForegroundColor Red
    $script:failed = $true
}
function Section([string]$title) { Write-Host "`n$title" }

Section 'Docker'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Fail 'docker не найден в PATH. Установите Docker Engine или Docker Desktop.'
} else {
    Ok "docker: $((docker --version) -join '')"

    $composeVersion = $null
    $composeVersion = docker compose version --short 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $composeVersion) {
        Fail 'docker compose v2 недоступен. Нужен плагин compose (docker compose, не docker-compose).'
    } else {
        Ok "docker compose: $composeVersion"
    }

    docker info --format '{{.ServerVersion}}' > $null 2>&1
    if ($LASTEXITCODE -ne 0) {
        Fail 'Docker-демон не отвечает. Запустите Docker Desktop (или службу docker) и повторите.'
    } else {
        Ok 'Docker-демон запущен'
    }
}

Section 'Конфигурация'

if (Test-Path '.env') {
    Ok '.env на месте'
} elseif (Test-Path '.env.example') {
    Copy-Item '.env.example' '.env'
    Ok '.env создан из .env.example (значения по умолчанию — только для локального стенда)'
} else {
    Fail 'Нет ни .env, ни .env.example — скопируйте репозиторий целиком.'
}

$requiredFiles = @(
    'docker-compose.yml',
    'docker-compose.api.yml',
    'docker/swift/Dockerfile',
    'docker/swift/entrypoint.sh',
    'docker/swagger/openapi.yaml',
    'docker/swagger/nginx.conf',
    'docker/filestash/config.json',
    'docker/otel-collector/config.yaml'
)
$missing = @($requiredFiles | Where-Object { -not (Test-Path $_) })
if ($missing.Count -eq 0) {
    Ok 'файлы для монтирования и сборки на месте'
} else {
    Fail "не хватает файлов: $($missing -join ', ')"
}

Section 'Образ Swift S3'

# SWIFT_IMAGE из .env переопределяет имя образа; по умолчанию — тег из compose.
$swiftImage = 'swift-s3api-aio:2.33'
if (Test-Path '.env') {
    $override = Select-String -Path '.env' -Pattern '^\s*SWIFT_IMAGE\s*=\s*(.+?)\s*$' |
        Select-Object -First 1
    if ($override) { $swiftImage = $override.Matches[0].Groups[1].Value.Trim('"', "'") }
}

if ($failed) {
    Warn 'проверка образа пропущена: Docker недоступен'
} else {
    docker image inspect $swiftImage > $null 2>&1
    if ($LASTEXITCODE -eq 0) {
        Ok "образ $swiftImage уже собран"
    } else {
        Warn "образа $swiftImage нет — он соберётся при первом запуске с --build (5-10 минут, нужен интернет)"
    }
}

Section 'Порты'

$standRunning = $false
if (-not $failed) {
    $running = docker compose ps --status running --quiet 2>$null
    $standRunning = [bool]$running
}

if ($standRunning) {
    Ok 'стенд уже запущен — проверка портов не нужна'
} elseif ($failed) {
    Warn 'проверка портов пропущена: Docker недоступен'
} else {
    $ports = @(
        @{ Port = 5433; Name = 'Postgres' },
        @{ Port = 8080; Name = 'Swift S3' },
        @{ Port = 8081; Name = 'Swagger UI' },
        @{ Port = 8082; Name = 'Filestash' },
        @{ Port = 8090; Name = 'API' },
        @{ Port = 4317; Name = 'OTLP gRPC' },
        @{ Port = 4318; Name = 'OTLP HTTP' },
        @{ Port = 13133; Name = 'OTel health' }
    )
    $busy = @()
    foreach ($entry in $ports) {
        $client = [System.Net.Sockets.TcpClient]::new()
        try {
            $task = $client.ConnectAsync('127.0.0.1', $entry.Port)
            if ($task.Wait(300) -and $client.Connected) {
                $busy += "$($entry.Port) ($($entry.Name))"
            }
        } catch {
            # Порт свободен: соединение отклонено.
        } finally {
            $client.Dispose()
        }
    }
    if ($busy.Count -eq 0) {
        Ok 'порты свободны'
    } else {
        Warn "заняты порты: $($busy -join ', '). Остановите занимающие их процессы или поменяйте порты в docker-compose.yml"
    }
}

Write-Host ''
if ($failed) {
    Write-Host 'Стенд запускать нельзя: исправьте ошибки выше.' -ForegroundColor Red
    exit 1
}

Write-Host 'Можно запускать:' -ForegroundColor Green
Write-Host '  docker compose up -d --build'
if ($warnings -gt 0) {
    Write-Host "(предупреждений: $warnings — они не блокируют запуск)" -ForegroundColor Yellow
}
exit 0
