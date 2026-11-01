# Проверка агентов: как понять, что Qwen и DeepSeek работают как ожидается

Главный принцип: **не верить самоотчёту модели**. Проверяются артефакты,
метаданные сессии, diff и тесты. Модель может написать «я Qwen», а фактически
быть другой моделью; доверять можно только тому, что вернул сервер OpenCode.

## 0. Проверка discovery (30 секунд)

В корне репозитория:

```bash
# Git Bash: отключить преобразование /api/... в путь Windows
MSYS_NO_PATHCONV=1 opencode --standalone api GET /api/agent
```

PowerShell:

```powershell
opencode --standalone api GET /api/agent
```

Ожидаемые агенты и модели:

| id | mode | providerID | model |
|---|---|---|---|
| `orchestrator` | `primary` | `opencode-go` | `qwen3.8-max` |
| `executor` | `subagent` | `opencode-go` | `deepseek-v4.1-flash` |
| `explorer` | `subagent` | `opencode-go` | `deepseek-v4.1-flash` |
| `reviewer` | `subagent` | `opencode-go` | `kimi-k3.1-pro` |

Быстрый фильтр:

```bash
MSYS_NO_PATHCONV=1 opencode --standalone api GET /api/agent \
  | grep -o '"id":"[^"]*"'
```

Если агентов нет — проверить, что команда запущена из корня проекта, а не из
домашнего каталога. Фоновый сервер OpenCode может быть привязан к другому
location; `--standalone` поднимает сервер именно для текущего каталога.

## 1. Проверка фактической модели

Модель в `results/*.md` — это **заявление агента**, а не доказательство.
Доказательство — метаданные сессии:

```bash
opencode session list
opencode api GET /api/session/<sessionID>
opencode api GET /api/session/<sessionID>/message
opencode api GET /api/experimental/session/stats
```

Что смотреть:

- событие/сообщение `ModelSelected` — какая модель реально выбрана;
- `Session.Message.Assistant` — модель, которая сгенерировала ответ;
- `SessionStats.ModelUsage` — сколько токенов ушло в какие модели;
- дочерняя сессия суб-агента: `sessionID` из результата `subagent`, затем
  `opencode api GET /api/session/<childID>`.

Критерий: у `orchestrator` — `qwen3.8-max`, у `executor` и `explorer` —
`deepseek-v4.1-flash`, у `reviewer` — `kimi-k3.1-pro`.

## 2. Проверка прав (негативные тесты)

Это важнее позитивных: агент должен **не мочь** выйти за роль.

| Агент | Запрос | Ожидание |
|---|---|---|
| `executor` | «поправь README.md» | отказ permission, `git status` без изменений |
| `executor` | «добавь файл в docs/» | отказ |
| `explorer` | «исправь этот баг в src/» | отказ, только чтение |
| `reviewer` | «поправь код» | отказ, только `results/` |
| `orchestrator` | «поменяй `src/...`» | отказ, это работа executor |
| `orchestrator` | «обнови README» | разрешено |

Проверять на scratch-задаче в отдельной ветке, чтобы отказ не оставил мусор.
После каждого теста:

```bash
git status --short
git diff --stat
```

Если запрещённое действие прошло — конфиг прав не работает, это блокер.

## 3. Canary-задачи (проверка контракта)

Маленькие задачи с заранее известным результатом. Запускать по одной, не
смешивать.

### 3.1. Explorer

```text
Найди, где в проекте используется pg_try_advisory_lock.
Верни file:line и краткое объяснение. Ничего не меняй.
```

Ожидание:

- точные ссылки на `ReconciliationService.cs` и `RetentionGcService.cs`;
- `results/explorer-1.md` существует;
- `git status` чистый;
- модель в сессии — `deepseek-v4.1-flash`.

### 3.2. Executor

```text
В файле tests/Reon.UnitTests/MultipartUploadServiceTests.cs
добавь один тест, который проверяет, что CompleteAsync возвращает
существующий файл при повторном вызове. Не меняй production-код.
Запусти dotnet test.
```

Ожидание:

- diff только в одном файле;
- `dotnet test` зелёный, вывод в `results/executor-1.md`;
- README/docs не тронуты;
- модель — `deepseek-v4.1-flash`.

### 3.3. Reviewer

Дай reviewer заведомо сломанный diff (например, тест без `await` или
перевёрнутое условие). Ожидание:

- находит проблему с `path:line`;
- не правит код;
- пишет `results/reviewer-1.md`;
- модель — `kimi-k3.1-pro`.

### 3.4. Orchestrator

```text
Составь brief для задачи: добавить health-check endpoint.
Код не пиши. Создай .agents/tasks/local-YYYYMMDD-01/ и заполни brief.md,
state.json, journal.ndjson.
```

Ожидание:

- есть цель, не-цели, файлы, критерии приёмки;
- `state.json` с lease;
- `src/` и `tests/` не изменены;
- модель — `qwen3.8-max`;
- orchestrator не пытается сам писать код.

## 4. Проверка handoff

Для канированной задачи проверить цепочку:

```text
brief.md → results/executor-1.md → results/reviewer-1.md → reduce.md
```

Критерии:

- `result` ссылается на brief и перечисляет файлы;
- `reviewer` не является автором;
- `reduce.md` ссылается на результаты и содержит решение;
- в `journal.ndjson` есть `claim`, `result`, `release`/`close`;
- `reduce.md` не появился раньше результатов.

## 5. Проверка параллельных сессий и lease

1. Открыть две сессии в одном проекте.
2. Обе пытаются захватить один `task-id`.
3. Ожидание: одна получает lease, вторая — отказ; `state.json` содержит
   одного владельца.
4. Проверить reclaim: вручную поставить `leaseUntil` в прошлое, вторая сессия
   должна перехватить задачу и записать `reclaim` в журнал.
5. Проверить, что старая сессия после потери lease прекращает запись.

## 6. Проверка fallback Go → Zen

1. Выполнить задачу на `opencode-go/...`.
2. Переключить модель на `opencode/...` (Zen).
3. Ожидание: в `journal.ndjson` появляется `model-fallback`, задача
   продолжается с тем же `attempt`, результат не теряется.
4. Проверить, что `results/` не перезаписан, а новый результат имеет новый
   `attempt`.

## 7. Метрики качества

Смотреть не «понравилось/не понравилось», а цифры:

| Метрика | Где | Норма |
|---|---|---|
| Модель на роль | session metadata | совпадает с registry |
| Шаги | `SessionStats` / `steps` | не упирается в лимит каждый раз |
| Retry | `session.retry.scheduled`, журнал | единичные, не постоянные |
| Permission denials | лог сессии | только в негативных тестах |
| Находки reviewer | `results/reviewer-*.md` | находит реальные проблемы, не стиль |
| Escaped defects | повторное ревью/тесты | 0 на канированных задачах |
| Стоимость | `opencode stats`, usage limits | не растёт линейно от числа суб-агентов |

## 8. Красные флаги

- Модель в сессии не совпадает с ожидаемой.
- Executor изменил `README.md` или `docs/`.
- Explorer или reviewer изменил `src/`.
- Orchestrator написал код вместо brief.
- Два владельца у одной задачи.
- `reduce.md` без результатов или без тестов.
- `results/` перезаписан вместо нового `attempt`.
- Fallback не записан в журнал.
- Задача закрыта, а `dotnet test` не запускался.

## 9. Минимальный прогон перед доверием

1. Discovery: все 4 агента на месте, модели совпадают.
2. Негативный тест прав: executor не может README, explorer не может src.
3. Canary explorer: точные `file:line`, чистый `git status`.
4. Canary executor: один файл, зелёные тесты, result заполнен.
5. Canary reviewer: находит заведомый баг, не правит код.
6. Canary orchestrator: brief + state + journal, без кода.
7. Два claim на одну задачу: один отказ.
8. Fallback: событие в журнале.

Если все 8 пунктов зелёные — контур работает как задумано. Если нет —
сначала чинить конфиг/права, а не «улучшать промпт».
