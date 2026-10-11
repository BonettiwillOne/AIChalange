# День 30 — приватный LLM API

Учебный HTTP-шлюз к локальной Ollama на VPS. Дополнительно сохранены консольный агент, MCP, память, RAG и сравнение локальных и облачных моделей.

## Архитектура

- `PrivateLlmApi/` — основной проект дня 30: ASP.NET Core / .NET 8 без сторонних пакетов. Клиент → SSH-туннель → API `127.0.0.1:8080` → Ollama `127.0.0.1:11434` → `qwen2.5:3b`.
- `POST /api/chat` принимает `{"message":"Привет"}` с заголовком `X-API-Key`. Ключ берётся из `PRIVATE_LLM_API_KEY`. Лимит: 5 авторизованных запросов в минуту, одна генерация одновременно, сообщение до 1000 единиц UTF-16, ответ до 64 токенов. `GET /health` проверяет шлюз без вызова модели.
- `W2D1/` — самостоятельный консольный проект .NET 6 с ModelContextProtocol 2.2.0, OpenAI и Ollama. Он не подключён к PrivateLlmApi.
- `documents/` — документы C# для построения RAG-индексов. `W2D1.sln` включает только консольный проект; API собирается отдельно.

Модели, индексы, отчёты, история диалогов, SDK и результаты сборки исключены. В копии MCP-клиента адрес личного VPS заменён на localhost:5000/5001. Для этих функций нужны собственные MCP-серверы; их исходников в рабочей папке нет.

## Сборка и запуск API на Linux VPS

Нужны SDK .NET 8 для сборки, ASP.NET Core Runtime 8 для запуска и установленная Ollama. Из корня репозитория:

```bash
dotnet restore PrivateLlmApi/PrivateLlmApi.csproj
dotnet build PrivateLlmApi/PrivateLlmApi.csproj -c Release --no-restore
dotnet publish PrivateLlmApi/PrivateLlmApi.csproj -c Release --self-contained false -o ./publish/private-llm-api
ollama pull qwen2.5:3b
read -r -s -p 'PRIVATE_LLM_API_KEY: ' PRIVATE_LLM_API_KEY
printf '\n'
export PRIVATE_LLM_API_KEY
dotnet ./publish/private-llm-api/PrivateLlmApi.dll
```

Введите собственный случайный ключ и храните его вне репозитория. Ollama должна работать на loopback; настройте `OLLAMA_NUM_PARALLEL=1`. API всегда слушает `127.0.0.1:8080`. Не открывайте порты 8080/11434 наружу. Для доступа с компьютера:

```bash
ssh -N -T -o ExitOnForwardFailure=yes -L 127.0.0.1:18080:127.0.0.1:8080 deploy@VPS_HOST
```

Замените SSH-пользователя и VPS_HOST своими данными. API доступен через `http://127.0.0.1:18080`; чат требует заголовок `X-API-Key`. Постоянный запуск через systemd, защищённое хранение ключа и ручные проверки описаны в [PrivateLlmApi/README.md](PrivateLlmApi/README.md).

## Дополнительный консольный проект

Для исходного `net6.0` нужны SDK и runtime .NET 6; пакеты восстанавливаются из NuGet:

```bash
dotnet restore W2D1/W2D1.csproj
dotnet build W2D1/W2D1.csproj -c Release --no-restore
dotnet run --project W2D1/W2D1.csproj -c Release --no-build
```

Запускайте из корня репозитория, чтобы находились документы. Для локального RAG скачайте `qwen2.5:3b` и `bge-m3` через `ollama pull`, затем в консоли выполните `/local-rag-index` и `/local-rag ваш вопрос`.

Облачные embeddings читают `OPENAI_API_KEY`. Обычный облачный чат и переписывание запросов в исходной реализации читают файл `Ключ` или `Ключ.txt` на рабочем столе пользователя. Этот файл создаётся отдельно вне репозитория. Для локальных режимов облачный ключ не нужен.

## Загрузка через браузер

Создайте репозиторий GitHub, выберите **Add file → Upload files** и загрузите содержимое этой папки, сохранив вложенные каталоги в корне репозитория. Проверьте, что скрытый `.gitignore` тоже загружен, затем подтвердите commit.

При подготовке приложение не запускалось; автоматические тесты не добавлялись. Сборка подготовленной копии не проверялась.
