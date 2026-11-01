# .agents — доска задач для AI-агентов

Это рабочая директория для синхронизации агентов. Правила и роли описаны в
`../AGENTS.md`; здесь — только практический минимум.

## Быстрый старт

Bash:

```bash
# 1. Скопировать шаблоны под новую задачу
mkdir -p .agents/tasks/REON-123/results
cp .agents/templates/brief.md  .agents/tasks/REON-123/brief.md
cp .agents/templates/state.json .agents/tasks/REON-123/state.json
cp .agents/templates/reduce.md .agents/tasks/REON-123/reduce.md

# 2. Заполнить brief.md: цель, не-цели, файлы, критерии приёмки
# 3. Захватить задачу (state.json + lease) и запушить
# 4. Работать только в своём worktree / ветке
# 5. Писать результаты в results/<agent>-<attempt>.md
# 6. Заполнить reduce.md и закрыть state.json
```

PowerShell:

```powershell
New-Item -ItemType Directory -Force .agents/tasks/REON-123/results | Out-Null
Copy-Item .agents/templates/brief.md   .agents/tasks/REON-123/brief.md
Copy-Item .agents/templates/state.json .agents/tasks/REON-123/state.json
Copy-Item .agents/templates/reduce.md  .agents/tasks/REON-123/reduce.md
```

## Файлы

| Путь | Назначение | Кто пишет |
|---|---|---|
| `registry.md` | Роли, модели, fallback | Человек/оркестратор |
| `templates/` | Шаблоны brief, state, result, reduce, journal | Оркестратор |
| `tasks/<task-id>/brief.md` | ТЗ задачи | Оркестратор, до claim |
| `tasks/<task-id>/state.json` | Владелец, lease, статус, файлы | Текущий владелец |
| `tasks/<task-id>/journal.ndjson` | Append-only события | Все, только append |
| `tasks/<task-id>/results/*.md` | Результаты map | Исполнители |
| `tasks/<task-id>/artifacts/` | Изображения, 3D, большие файлы | Asset-агенты |
| `tasks/<task-id>/reduce.md` | Итоговое решение | Оркестратор |
| `locks/` | Локальные lock-файлы, не коммитятся | Локальные сессии |

## Claim (атомарный захват)

PowerShell:

```powershell
$path = ".agents/tasks/REON-123/state.json"
try {
  $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::CreateNew)
  $fs.Close()
} catch [System.IO.IOException] {
  Write-Error "Задача уже захвачена: $path"
}
```

Bash:

```bash
set -o noclobber
if ! : > .agents/tasks/REON-123/state.json; then
  echo "Задача уже захвачена" >&2
  exit 1
fi
```

Для синхронизации между машинами одного локального файла мало: после захвата
нужно закоммитить `state.json` и запушить. Если push отклонён — перечитать
состояние и выбрать другую задачу.

## Запреты

- Не хранить секреты и токены.
- Не перезаписывать `brief.md` после claim — только `brief.v2.md`.
- Не перезаписывать чужие `results/`.
- Не писать в `README.md` и `docs/` из роли executor.
- Не мержить без зелёных `dotnet build` и `dotnet test`.
