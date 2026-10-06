# Windows Cleanup Manager (`wcm`)

ncdu-подобный TUI для полуавтоматической очистки диска в Windows.
Сканирует диск в несколько потоков, предлагает что удалить (по правилам или по модели [Jev](https://openrouter.ai/labs/jev)),
вы отмечаете, подтверждаете — удаляет в корзину или навсегда. Поддерживает файлы, папки, Docker-образы, тома и build cache.

## Запуск

```powershell
dotnet run --project src/Wcm                 # TUI
dotnet run --project src/Wcm -- scan C:\     # без TUI: только список предложений (ничего не удаляет)
dotnet run --project src/Wcm -- scan D:\Dev --no-ai --json out.json
dotnet run --project src/Wcm -- rules        # активные правила

# один exe
dotnet publish src/Wcm -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

Для Jev нужен ключ OpenRouter: `setx OPENROUTER_API_KEY sk-or-...` или поле `openRouterApiKey` в конфиге.
Без ключа работают только правила (и ранее закэшированные ответы Jev).

## Как принимается решение

1. **Protect** — `C:\Windows`, `Program Files`, Documents/Desktop/…, `.git`, диск Docker и т.п. Никогда не предлагаются и не удаляются
   (кроме явных исключений вроде `Windows\Temp`, `SoftwareDistribution\Download`).
2. **Правила** — встроенные (`src/Wcm/Classification/builtin-rules.json`) и ваши (`%APPDATA%\Wcm\rules.json`, приоритетнее).
3. **Кэш решений Jev** (`%APPDATA%\Wcm\jev-cache.json`) — по пути + отпечатку содержимого, TTL 90 дней.
4. **Jev** — только для оставшихся крупных объектов (папки ≥ 1 GB, файлы ≥ 2 GB), с «спуском» к тем подпапкам,
   где реально лежит объём. Лимиты на скан: 100 запросов и $0.05; перед отправкой спрашивается подтверждение.
   В модель уходят путь (профиль заменён на `%USERPROFILE%`), размеры и имена крупнейших вложенных элементов.

Ответ Jev: `deletable ≥ 0.85` и безопасная категория → отмечено; `0.6–0.85` или рискованная категория → предложено без отметки.
В списке `R` превращает любое предложение в постоянное правило, `N` — «больше не предлагать».

## Клавиши

| Экран | Клавиши |
|---|---|
| Меню | `↑↓` выбор, `Enter` скан, `I` вкл/выкл Jev, `R` перечитать конфиг, `Q` выход |
| Список | `Space` отметить, `A` все safe, `U` снять всё, `S` сортировка, `Enter`/`B` дерево, `R` правило, `N` не предлагать, `Del`/`D` в корзину, `Shift+Del`/`X` навсегда |
| Дерево | `Enter`/`→` войти, `←`/`Backspace` вверх, `Space` отметить вручную, `Del`/`X` удалить, `Q` к списку |

## Формат правила

```jsonc
{
  "id": "my-unity-cache",
  "match": ["**\\Library"],            // ** — любая глубина, * — часть имени, %ENV% раскрываются
  "kind": "dir",                        // dir | file | any
  "action": "suggest",                  // suggest | never | protect | container
  "when": { "siblingExists": "ProjectSettings", "minSize": "100MB", "olderThanDays": 30 },
  "category": "build",
  "safety": "safe",                     // safe — отмечается сразу, review — нет
  "contents": false,                    // true — удалять содержимое, саму папку оставить
  "reason": "кэш Unity"
}
```

Журнал удалений: `%APPDATA%\Wcm\deletions.log`.

Docker: место освобождается внутри `docker_data.vhdx`, сам файл может не уменьшиться —
`wsl --shutdown`, затем `Optimize-VHD` / `diskpart compact vdisk`.
