# PrivateLlmApi — день 30

Минимальный шлюз .NET 8 без сторонних пакетов. Единственный адрес прослушивания: `127.0.0.1:8080`; переменные `ASPNETCORE_URLS` и аргумент `--urls` не меняют привязку Kestrel. Ollama вызывается по `http://127.0.0.1:11434/api/chat`.

`POST /api/chat`: `{"message":"Привет"}` → `{"response":"Ответ модели"}`. Обязателен заголовок `X-API-Key`. Только сервер задаёт `qwen2.5:3b`, `num_ctx=512`, `num_predict=64`, `stream=false`; дополнительные поля клиента игнорируются. История диалога не хранится. Длина — до 1000 единиц UTF-16 (`string.Length`), тело HTTP — до 16 КиБ (при превышении 413).

Общий fixed-window limiter: 5 авторизованных обращений за окно 60 секунд, без очереди, включая запросы с ошибкой валидации или 503. На границе окон возможны последовательные всплески. Неверный ключ возвращает 401 и не расходует лимит. Генерация одна на процесс, при занятости — 503. Запускайте только один экземпляр шлюза. `/health` возвращает `{"status":"ok"}` без ключа и вызова Ollama; это проверка шлюза, а не модели.

Ошибки Ollama → 502, таймаут 180 секунд → 504. Внутренние исключения, заголовки и тексты диалогов не логируются. При разрыве соединения клиента слот сохраняется до завершения HTTP-вызова Ollama или таймаута. После таймаута отмена в Ollama может завершиться не мгновенно: шлюз не может гарантировать состояние чужого процесса. Для ограничения самой Ollama настройте `OLLAMA_NUM_PARALLEL=1` (см. ниже).

## Сборка и публикация на Windows

Нужен SDK .NET 8. Из `<папка репозитория>`:

```powershell
dotnet build .\PrivateLlmApi\PrivateLlmApi.csproj -c Release
dotnet publish .\PrivateLlmApi\PrivateLlmApi.csproj -c Release -r linux-x64 --self-contained false -o .\PrivateLlmApi\publish\linux-x64
```

Публикация framework-dependent: на VPS нужен ASP.NET Core Runtime 8, а SDK не нужен. Workstation GC уменьшает накладные расходы по памяти; модель всё равно использует основную часть RAM/swap. Для Ubuntu 24.04 x64:

```bash
sudo apt-get update
sudo apt-get install -y aspnetcore-runtime-8.0
dotnet --list-runtimes
ollama list
```

В списке должна присутствовать `qwen2.5:3b`. Ollama должна слушать только loopback; порты 8080 и 11434 в firewall открывать не нужно.

## Перенос по SCP

Замените `deploy@VPS_HOST` своим SSH-пользователем и адресом. На Windows:

```powershell
ssh deploy@VPS_HOST 'mkdir -p ~/private-llm-upload'
scp -r .\PrivateLlmApi\publish\linux-x64\* deploy@VPS_HOST:private-llm-upload/
```

На VPS:

```bash
sudo useradd --system --user-group --no-create-home --shell /usr/sbin/nologin private-llm
sudo install -d -o root -g root -m 755 /opt/private-llm-api
sudo cp -r ~/private-llm-upload/. /opt/private-llm-api/
sudo chown -R root:root /opt/private-llm-api
sudo chmod -R go-w /opt/private-llm-api
```

При обновлении сначала остановите сервис, скопируйте новую публикацию и запустите его снова.

## Секрет и systemd

Создайте случайный ключ без его вывода в терминал или записи в историю команд. Этот файл — защищённое хранилище среды systemd, не конфигурация проекта; секрет нельзя коммитить или помещать в unit:

```bash
sudo install -d -o root -g root -m 700 /etc/private-llm-api
sudo sh -c 'umask 077; printf "PRIVATE_LLM_API_KEY=%s\n" "$(openssl rand -hex 32)" > /etc/private-llm-api/environment'
```

Для переноса значения в менеджер паролей можно открыть файл через `sudo nano /etc/private-llm-api/environment`. Не публикуйте его содержимое. systemd читает файл от root и передаёт ключ непривилегированному процессу через окружение. Не используйте команды диагностики, печатающие окружение.

Создайте `/etc/systemd/system/private-llm-api.service`:

```ini
[Unit]
Description=Private local LLM gateway
After=network.target ollama.service

[Service]
Type=simple
User=private-llm
Group=private-llm
WorkingDirectory=/opt/private-llm-api
ExecStart=/usr/bin/dotnet /opt/private-llm-api/PrivateLlmApi.dll
EnvironmentFile=/etc/private-llm-api/environment
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_EnableDiagnostics=0
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
UMask=0077

[Install]
WantedBy=multi-user.target
```

Для Ollama выполните `sudo systemctl edit ollama` и добавьте:

```ini
[Service]
Environment="OLLAMA_HOST=127.0.0.1:11434"
Environment="OLLAMA_NUM_PARALLEL=1"
Environment="OLLAMA_MAX_LOADED_MODELS=1"
```

Затем:

```bash
sudo systemctl daemon-reload
sudo systemctl restart ollama
sudo systemctl enable --now private-llm-api
sudo systemctl status private-llm-api --no-pager
sudo ss -ltnp 'sport = :8080'
curl http://127.0.0.1:8080/health
```

Ожидается только `127.0.0.1:8080`. Не запускайте другие генерации через прямой API Ollama одновременно с демонстрацией шлюза.

## SSH-туннель с ноутбука

В отдельном PowerShell, оставьте команду работающей:

```powershell
ssh -N -T -o ExitOnForwardFailure=yes -L 127.0.0.1:18080:127.0.0.1:8080 deploy@VPS_HOST
```

Локальный порт 18080 выбран, чтобы не конфликтовать с Windows-запуском. SSH шифрует трафик, HTTP остаётся на loopback с обеих сторон.

## Ручные проверки (PowerShell 7)

В другом терминале безопасно введите ключ; не вписывайте его литералом в команды:

```powershell
$secureKey = Read-Host 'API key' -AsSecureString
$env:PRIVATE_LLM_API_KEY = [System.Net.NetworkCredential]::new('', $secureKey).Password
$base = 'http://127.0.0.1:18080'
$headers = @{ 'X-API-Key' = $env:PRIVATE_LLM_API_KEY }
function Send-Chat([string]$Message, [hashtable]$Headers = $headers) {
    $body = @{ message = $Message } | ConvertTo-Json -Compress
    $r = Invoke-WebRequest "$base/api/chat" -Method Post -Headers $Headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -SkipHttpErrorCheck
    "HTTP $([int]$r.StatusCode) $($r.Content)"
}
Invoke-RestMethod "$base/health"
Send-Chat 'Ответь одним словом: привет'  # 200 при работающей Ollama
Send-Chat 'Привет' @{ 'X-API-Key' = 'intentionally-wrong' } # 401
Send-Chat ('а' * 1001) # 400 с объяснением
```

Быстрая детерминированная проверка 429 без генерации (после ожидания нового окна):

```powershell
Start-Sleep -Seconds 61
1..6 | ForEach-Object { Send-Chat ('а' * 1001) }
# Первые пять: 400; шестой: 429. Эти обращения тоже расходуют лимит.
```

Повторные успешные обращения: дождитесь 61 секунды и вызовите `Send-Chat 'Привет'`, затем после получения ответа `Send-Chat 'Сколько будет 2+2?'`. Каждый запрос независим. Ответ ограничен 64 токенами и может быть обрезан. При холодной загрузке на 2 ГБ RAM ответ может занять длительное время или закончиться 504.

Для проверки 503 после нового окна запустите запрос в первом терминале и, пока он выполняется, отправьте второй в другом терминале с тем же ключом. Второй должен вернуть 503; если первый уже закончился, повторите опыт. При исчерпанном лимите раньше сработает 429. Для проверки 502 можно вручную остановить Ollama, отправить запрос и затем снова запустить Ollama.

## Запуск на Windows

Нужна локальная Ollama с той же моделью на порту 11434. Отсутствие модели не мешает `/health`, но chat вернёт 502. Ключ задайте через `Read-Host`, как выше. Точная команда из рабочей папки, используя установленный SDK .NET 8:

```powershell
Set-Location '<папка репозитория>'
dotnet run --project .\PrivateLlmApi\PrivateLlmApi.csproj -c Release --no-launch-profile
```

Приложение всегда слушает `127.0.0.1:8080`. Для локальных проверок задайте `$base = 'http://127.0.0.1:8080'`. После работы удалите секрет из текущей сессии: `Remove-Item Env:PRIVATE_LLM_API_KEY; $headers.Clear(); $secureKey.Dispose()`.

Официальные справочники: [ASP.NET Core Rate Limiter](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-8.0), [Ollama chat API](https://docs.ollama.com/api/chat).
