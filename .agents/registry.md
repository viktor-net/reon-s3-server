# Реестр агентов

| Роль | Go (primary) | Zen (fallback) | Режим | Права |
|---|---|---|---|---|
| `orchestrator` / scribe | `opencode-go/qwen3.8-max` | `opencode/qwen3.8-max` | primary | brief, состояние, README/docs, reduce; код не пишет |
| `executor` | `opencode-go/deepseek-v4.1-flash` | `opencode/deepseek-v4.1-flash` | subagent | код и тесты в границах brief |
| `explorer` | `opencode-go/deepseek-v4.1-flash` | `opencode/deepseek-v4.1-flash` | subagent | только чтение и поиск |
| `reviewer` | `opencode-go/kimi-k3.1-pro` | `opencode/claude-sonnet-5-5` | subagent | чтение, diff, тесты; без правок |
| `asset-image` | image-модель | — | tool | `experimental_image_generate` |
| `modeler-3d` | TBD | — | зарезервировано | 3D через MCP/plugin, пока нет |

## Правила выбора

- Основной канал — OpenCode Go; при исчерпании лимитов — Console/Zen.
- Переключение фиксируется в `journal.ndjson` (`event: model-fallback`).
- Дешёвая модель допустима для map/explore, но не для финального reduce.
- Reviewer не должен совпадать с автором изменений.
- Модель не выбирает роль сама: роль задаётся `brief.md` и конфигом агента.

## Проверка модели

Перед сменой модели убедиться, что она есть в каталоге:

```bash
opencode models | grep -E 'qwen3.8-max|deepseek-v4.1-flash|kimi-k3.1-pro'
```

Не хардкодить модели, которых нет в каталоге текущего провайдера.
