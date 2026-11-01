# Result: <agent> / attempt <n>

- task: `<task-id>`
- agent: `<executor|explorer|reviewer>`
- model: `opencode-go/...`
- branch: `agent/<task-id>`
- worktree: `.worktrees/<task-id>-<agent>`
- commit: `<sha или пусто>`
- started: `<ISO-8601>`
- finished: `<ISO-8601>`

## Что сделано

Кратко и проверяемо. Без «улучшил» и «поправил всё».

## Файлы

- `path/to/file` — что изменено.
- `path/to/file` — что добавлено.

## Проверка

```text
dotnet build  -> успешно
dotnet test   -> N passed, 0 failed
```

Если что-то не запускалось — прямо написать почему и какой риск.

## Открытые вопросы

- ...

## Риски и ограничения

- ...

## Предложение для README/docs

Текст, который scribe может применить после reduce. Если не нужно — «нет».
