# yt-dlp WebUI

ASP.NET Core 10 Minimal API и React составляют одно приложение: Kestrel раздает API и собранный UI. Требования целевого
MVP находятся в [OpenSpec](openspec/README.md), согласованный переход на Authentik и отправку файлов —
в [активном change](openspec/changes/authentik-sso-and-file-uploads/proposal.md).

Реализован вход через Authentik по OIDC, административный доступ по группе, сохраняемые серверные cookie-сессии и
CSRF-защита команд. PostgreSQL мигрирует до приема запросов; сессии, их истечение и отзыв сохраняются при
перезапуске с прежними БД и ключами Data Protection.

Библиотека читает все объекты настроенного S3 bucket, включая внешние файлы и все страницы listing. Администратор может
отправить произвольный локальный файл; посетитель скачивает точные байты через бэкенд. Одинаковые имена получают разные
ключи. Внешние файлы удаляются синхронно, а собственные публикации — через общую очередь с проверкой состояния
агрегата и сохранением истории. Worker обрабатывает сохраненные команды и согласует внешние результаты при ручном повторе.

API заданий сохраняет URL, попытки, этапы и команды в PostgreSQL; очередь переживает перезапуск. Конкурентные команды
защищены уникальностью и Version, а claim обеспечивает одну исполняемую попытку. Настоящий BackgroundService последовательно
запускает yt-dlp, FFmpeg/FFprobe, отправляет проверенный результат в S3 и очищает только рабочий каталог задания.
План, готовые артефакты, SHA-256, прогресс и история этапов сохраняются. До приема HTTP и запуска worker прерванные
операции получают Failed/interrupted для ручного повтора, а ожидающие задания остаются в очереди. Прогресс не симулируется. Интерфейс поддерживает телефон/десктоп,
системную тему и EN/RU по браузеру.

## Структура

```text
Ytdlp.Ui.slnx
Directory.Build.props
Directory.Packages.props
global.json
Dockerfile
compose.yaml
.github/workflows/ci.yml
openspec/{README.md,design.md,specs/,changes/}
src/
  Directory.Build.props
  Ytdlp.Ui/
    Features/{Auth,Downloads,Library,Health,Hello}/
    Domain/
    Infrastructure/{Authentication,Media,Persistence,Storage}/
    Configuration/
  Ytdlp.Ui.Client/
    package.json
    package-lock.json
    src/
    dist/  # результат сборки, не коммитится
tests/
  Directory.Build.props
  Ytdlp.Ui.Tests/             # автономные unit/component/process tests без Docker
  Ytdlp.Ui.ProcessFixture/    # .NET helper настоящих process tests, не test project
  Ytdlp.Ui.IntegrationTests/  # HTTP/OIDC/PostgreSQL и отдельный Storage namespace
    SetupBase.cs
    SetupGlobal.cs
    Tools/CustomWebApplicationFactory.cs
```

Версии NuGet управляются через CPM в `Directory.Packages.props`, общие тестовые зависимости находятся в
`tests/Directory.Build.props`. Интеграционные зависимости подключены только соответствующему проекту. Каждый вложенный
props импортирует родительский; solution содержит проекты и общие файлы как Solution Items.

## Настройка Authentik и конфигурация

В существующем Authentik создайте confidential OAuth2/OIDC provider с Authorization Code и приложением для WebUI.
Выберите per-provider issuer и зарегистрируйте точный внешний HTTPS callback `https://<адрес-приложения>/signin-oidc`.
Scopes `openid` и `profile` должны возвращать `sub` и настроенный claim групп, в том числе через UserInfo. Участники
выбранной группы получают административный доступ; остальные видят публичный каталог.

| Параметр                             | Назначение                                                                                   |
|--------------------------------------|----------------------------------------------------------------------------------------------|
| `Oidc__Authority`                    | HTTPS issuer приложения, например `https://authentik.example.test/application/o/ytdlp-ui/`   |
| `Oidc__ClientId`                     | Идентификатор confidential client                                                            |
| `Oidc__ClientSecret`                 | Секрет клиента только на бэкенде                                                             |
| `Oidc__GroupsClaim`                  | Claim с группами, обычно `groups`                                                            |
| `Oidc__AdminGroup`                   | Группа администраторов, например `ytdlp-ui-admins`                                           |
| `ConnectionStrings__AppDatabase`     | PostgreSQL, например `Host=db.example.test;Database=ytdlp_ui;Username=ytdlp_ui;Password=...` |
| `Paths__DataProtectionKeysDirectory` | Постоянный каталог ключей; в контейнере `/data/keys`                                         |
| `Paths__WorkDirectory`               | Временные файлы операций на volume `/data/work`; каталог доступен UID 1654                   |
| `MediaTools__YtDlpPath`              | Исполняемый yt-dlp, по умолчанию `yt-dlp`                                                    |
| `MediaTools__FfmpegPath`             | Исполняемый FFmpeg, по умолчанию `ffmpeg`                                                    |
| `MediaTools__FfprobePath`            | Исполняемый FFprobe, по умолчанию `ffprobe`                                                  |
| `MediaTools__NodePath`               | Исполняемый Node.js, по умолчанию `node`; передается yt-dlp как JS runtime                   |
| `Worker__Enabled`                    | По умолчанию `true`; `false` отключает обработку, сохраняя API и очередь                     |
| `Worker__PollIntervalMilliseconds`   | Опрос ожидающей очереди, по умолчанию 1000; положительное целое                              |
| `S3__Endpoint`                       | URL S3/MinIO, например `https://minio.example.test`                                          |
| `S3__Region`                         | Регион подписи запросов, по умолчанию `us-east-1`                                            |
| `S3__Bucket`                         | Существующий bucket, управляемый вне приложения                                              |
| `S3__AccessKey`, `S3__SecretKey`     | Серверные учетные данные S3                                                                  |
| `S3__ForcePathStyle`                 | Адресация path style для MinIO, по умолчанию `true`                                          |
| `ReverseProxy__KnownProxies__0`      | Доверенный IP reverse proxy; дополнительные адреса задаются индексами                        |
| `ReverseProxy__KnownNetworks__0`     | Доверенная CIDR-сеть вместо отдельных адресов при необходимости                              |
| `ReverseProxy__AllowedHosts__0`      | Разрешенный внешний host из X-Forwarded-Host                                                 |

Обязательные OIDC-поля проверяются при запуске; отсутствующая группа или секрет не включают локальную авторизацию.
Демонстрационные значения `appsettings.json` следует заменить. `Admin`/`Jwt`, локальный пароль, собственные JWT и
refresh API больше не используются. Токены OIDC и client secret не передаются React и не сохраняются в браузерном
хранилище.

В production используйте HTTPS, обычно с завершением TLS на существующем reverse proxy. Обработка `X-Forwarded-For`,
`X-Forwarded-Proto` и `X-Forwarded-Host` включается только при заданных доверенных IP/сетях; без них заголовки не
изменяют адрес callback. Ограничьте `AllowedHosts` именем публичного приложения и укажите реальные адреса доверенной
инфраструктуры. Discovery/JWKS/token/UserInfo Authentik должны быть доступны бэкенду.

Сессия абсолютная, 12 часов от входа, без sliding и «Запомнить меня». Браузер получает HttpOnly cookie; в production она
Secure и SameSite=Lax. Cookie общая для вкладок браузера; каждая вкладка проверяет вход при загрузке и возвращении на
страницу. Изменение группы в Authentik отражается при новом SSO-входе; мгновенный внешний отзыв не заявляется. Выход
отзывает только текущую сессию приложения, не завершает Authentik/другие приложения и не отменяет фоновые задания.

`GET /api/auth/session` возвращает отдельный случайный `sessionId` для защиты UI от поздних ответов. Это идентификатор
интерфейсной сессии, который не совпадает с ключом серверного ticket и не дает доступа через header/query/cookie. После
выхода, нового входа или `401` закрытые данные очищаются; ответы прежней сессии не возвращают их и не закрывают новые
формы.

## Сборка и запуск

Требуются .NET SDK **10.0.403**, Node.js **24.19.0** и npm в PATH. SDK строго зафиксирован в `global.json` и согласован
с Docker; CI читает тот же файл. Источник NuGet ограничен `nuget.org` в `NuGet.Config`.

```powershell
npm --prefix src/Ytdlp.Ui.Client ci --no-audit --no-fund
npm --prefix src/Ytdlp.Ui.Client run build
dotnet restore Ytdlp.Ui.slnx
dotnet build Ytdlp.Ui.slnx -c Release --no-restore
```

По умолчанию .NET не запускает npm; сборка только API допустима без `dist`. Готовый React включается в `wwwroot`
build/publish без копирования в исходники. Совместная сборка включается явно:
`dotnet build Ytdlp.Ui.slnx -c Release -p:BuildFrontend=true`. Отдельный target —
`dotnet build src/Ytdlp.Ui/Ytdlp.Ui.csproj -t:BuildFrontend`.

После изменения `dist` повторите `dotnet build` перед .NET-тестами и `publish --no-build`: manifest static assets
связан с конкретной сборкой UI. Не пересобирайте `dist` параллельно раздаче приложения или тестам его assets.

Пример локальных путей после задания реальных OIDC-параметров в окружении:

```powershell
$env:ConnectionStrings__AppDatabase = 'Host=localhost;Port=15432;Database=ytdlp_ui;Username=ytdlp_ui;Password=...'
$env:Paths__DataProtectionKeysDirectory = Join-Path (Get-Location) 'data/keys'
$env:Paths__WorkDirectory = Join-Path (Get-Location) 'data/work'
dotnet run --project src/Ytdlp.Ui -c Release --no-build
```

Профиль разработки слушает `http://localhost:5080`; для локального OIDC настройте подходящий callback/proxy в своей
среде. В production callback — HTTPS. Для React разработки `npm --prefix src/Ytdlp.Ui.Client run dev` использует Vite
proxy к бэкенду на 5080; рекомендуемый SSO-сценарий — единый origin через Kestrel/reverse proxy.

## API

| Метод и маршрут                   | Доступ и запрос                                                                   | Ответ                                                                                                                                                                               |
|-----------------------------------|-----------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `GET /api/auth/login?returnUrl=/` | Навигация браузера; returnUrl только локальный                                    | OIDC redirect в Authentik; `400` для внешнего returnUrl                                                                                                                             |
| `/signin-oidc`                    | Callback стандартного OIDC middleware                                             | После проверки — возврат в библиотеку; безопасный `authError=access_denied` или `login_failed` при отказе, без новых полномочий                                                     |
| `GET /api/auth/session`           | Публичный, cookie опциональна                                                     | `200 { authenticated, sessionId }`; без действующей административной сессии false/null                                                                                              |
| `GET /api/auth/csrf`              | Администратор, cookie                                                             | `200 { requestToken }` для X-CSRF-TOKEN                                                                                                                                             |
| `POST /api/auth/logout`           | Администратор, cookie + CSRF                                                      | `204` после сохраненного отзыва                                                                                                                                                     |
| `GET /api/library`                | Публичный                                                                         | Все страницы S3 как плоский массив `{ id, name, title, size, lastModified, contentUrl }`                                                                                            |
| `GET /api/library/{id}/content`   | Публичный                                                                         | Поток S3 с attachment, octet-stream, Content-Length и nosniff; `404` при подтвержденном отсутствии                                                                                  |
| `PUT /api/library/{id}/title`     | Администратор, cookie + CSRF                                                      | `{ "title": "…" }` или `{ "title": null }`, `204` при сохранении или сбросе, `400` при неверном запросе, `404` при отсутствии файла, `503` при недоступности S3                     |
| `DELETE /api/library/{id}`        | Администратор, cookie + CSRF                                                      | Своя публикация: `202 { downloadId }` с Location после durable enqueue; внешний/уже удаленный объект: `204`; `409` при DB/state/pub конфликте; внешнее S3-удаление может дать `503` |
| `POST /api/library/uploads`       | Администратор, cookie + CSRF; multipart/form-data с единственной file-part `file` | `201` с `{ id, name, title, size, lastModified, contentUrl }` после проверки S3; `400 invalid_upload` или `503 storage_unavailable`                                                 |
| `GET /api/downloads`              | Администратор                                                                     | Сохраняемые задания с попытками, этапами и allowedActions                                                                                                                           |
| `GET /api/downloads/{id}`         | Администратор                                                                     | Задание или `404`                                                                                                                                                                   |
| `POST /api/downloads`             | Администратор, cookie + CSRF; `{ url }`                                           | `201` с Location; `202` для повтора удаленного результата; `400 invalid_url`, `409 download_already_exists`                                                                         |
| `POST /api/downloads/{id}/retry`  | Администратор, cookie + CSRF                                                      | `202`, новая попытка; `404` или `409 invalid_state`                                                                                                                                 |
| `POST /api/downloads/{id}/cancel` | Администратор, cookie + CSRF                                                      | `202`; `404` или `409 invalid_state`                                                                                                                                                |
| `GET /api/hello`                  | Публичный                                                                         | `200 text/plain`, `Hello World`                                                                                                                                                     |
| `GET /health/live`                | Публичный                                                                         | `200 text/plain Healthy`: HTTP-процесс отвечает, без проверки зависимостей                                                                                                          |
| `GET /health/ready`               | Публичный                                                                         | `200 Healthy` или `503 Unhealthy`: БД, запись в work/keys и легкое чтение bucket; без внутренних подробностей                                                                       |

Административный API возвращает `401` без сессии и `403` без прав, без HTML-перенаправления. CSRF проверяется до
побочных действий; неверное подтверждение дает `400 antiforgery_failed`. Auth-ответы не кешируются. Неперехваченная
ошибка — `500 application/problem+json` без текста исключения и секретов. Неизвестный `/api/*` возвращает `404` без
React HTML; отсутствующий static asset — `404`, маршруты UI без расширения поддерживают SPA fallback.

Состояние задания сохраняет прежние поля интерфейса:
`{ id, url, title, state, resumeState, progress, createdAt, errorCode, error, allowedActions, attempts }`;
добавлены `currentAttemptId`, `publicationId`, `publicationState`, `failedState`, `cancelRequested`, `version`.
Попытка содержит `{ number, state, createdAt, error, id, reason, status, initialState, startedAt, finishedAt, errorCode, continuationReasonCode, stages }`;
этап — `{ id, stage, status, startedAt, finishedAt, progress, errorCode, error, artifactId, reusedFromStageId }`.
Прогресс берется из сохраненной записи выполняемого этапа: для Download — фактические байты yt-dlp при известном
общем размере всех дорожек, иначе `null`. Prepare/Upload/Cleanup показывают неопределенный прогресс до подтверждения
успеха; завершенный этап получает 100.

Исходный URL хранится точно как принят, отдельно от нормализованного. Удаляются только внешние пробелы, нормализуются
scheme/host/default port; raw path, query/order и fragment сохраняются. Полный URL остается text; уникальный ключ —
SHA-256 normalized UTF-8 из 32 bytes, вычисляемый persistence при каждой записи. После lookup сравнивается точная строка:
теоретический hash conflict дает `409 url_conflict` без чужого ID. Дубликат дает `409 download_already_exists` и
`existingDownloadId`. Для Deleted и подтвержденного S3-отсутствия Completed результатом становится новая попытка того
же ID с новым publication/workspace UUID. Failed/Canceled требуют явного retry. Ответы 201/202 включают Location.

Queue claim сериализуется коротким PostgreSQL advisory transaction lock, проверяет отсутствие исполняемой попытки и
выбирает FIFO по QueuedAt/Id; сеть/процессы внутри транзакции не выполняются. Optimistic Version и unique active-attempt
откатывают устаревшие команды вместе с историей/intent. Отмена идемпотентна и не стирает cleanup intent. CleaningUp и
retry очистки не допускают отмену. Worker не держит транзакцию во время процессов, файлового или сетевого ввода-вывода.

## Обработка медиа

Production-образ содержит yt-dlp **2026.10.26**, EJS **0.8.0**, Node.js **24.19.0** и FFmpeg/FFprobe **6.1.1**.
При запуске вне контейнера установите готовые инструменты либо задайте их пути из таблицы. Поддержка EJS и Node.js >=22
нужны для актуальных JS-проверок источников; бэкенд явно передает yt-dlp `--js-runtimes node:<путь>`.

Worker получает метаданные до скачивания и отклоняет playlist, текущий/запланированный эфир. Выбирается видео с аудио
до 1080p с предпочтением MP4. Неизвестные codec/height у прямого MP4 допустимы как fallback, если нет заведомо
подходящего варианта; скачанный файл проверяется FFprobe. Обложка `attached_pic` не считается видеодорожкой.
Совмещенный файл не объединяется заново. Раздельные дорожки скачиваются отдельными вызовами с точным
`all[format_id="..."]`; FFmpeg использует только `-c copy`, контейнер определяется по фактическим кодекам.
WebM требует VP8/VP9/AV1 с Opus/Vorbis; alias `matroska,webm` сам по себе этого не доказывает. Расширение соответствует
фактическому контейнеру, несовместимые дорожки сохраняются в MKV без перекодирования.

Процессы запускаются без shell через `ArgumentList`, с `--ignore-config` и одним URL после `--`. Сохраняется исходная
строка URL, а процесс получает ее с удаленными внешними пробелами. stdout/stderr читаются одновременно: диагностика
ограничена 16 384 символами, progress stream не имеет накопительного лимита длительности/вывода. Буфер metadata JSON — 8 388 608 символов;
это техническая граница ответа, а не ограничение размера или длительности ролика. Отмена останавливает дерево
процессов и ожидает завершения чтения до очистки.

Рабочие файлы находятся в `Paths:WorkDirectory/downloads/<download-id>/<workspace-generation>/`; пользовательские имена
не становятся локальными путями. Начальная миграция включает необязательное поле плана `MediaPlanJson`.
Перед внешними действиями сохраняются этап/намерение, артефакт и multipart identity. Отправка использует поток и
ограниченную память, уникальный ключ `downloads/<download-id>/<publication-id>/<safe-name>.<ext>`, metadata UUID/размер/SHA-256.
После Complete проверяются размер и metadata S3; только затем допускаются CleaningUp и Completed.

Ошибка объединения сохраняет дорожки, ошибка отправки — подготовленный файл; ручной повтор использует их без нового
скачивания. При ошибке CleaningUp опубликованный объект остается доступен; повтор выполняет очистку. Обычная отмена
сохраняет intent, останавливает активный процесс и очищает свои файлы. Выход из SSO не отменяет worker.
После старта StartupRecovery атомарно помечает прерванные Downloading/Preparing/Uploading/CleaningUp/Deleting/Canceling
как Failed/interrupted с прежними FailedState/ResumeState и историей. Ожидающие задания выполняются дальше; прерванные
не запускаются автоматически. Ручной retry создает новую попытку того же задания. Готовые файлы проверяются по размеру
и SHA-256, пригодные этапы ссылаются на прежние через `reusedFromStageId`. Потеря/повреждение файла возвращает к самому
раннему необходимому Download/Prepare с объяснением в истории; проверка повторяется при исполнении после ожидания очереди.
Cleanup/Delete/Cancel не возвращаются к скачиванию. После Deleted/Canceled используются новые publication/workspace UUID
и свежий план; запоздалые события прежней попытки не получают доступ к новым файлам.

Download пишет во временный файл и после успеха процесса/FFprobe атомарно переименовывает его; Prepare сохраняет будущий
путь до copy/merge и также завершает файл атомарно. Если готовый файл появился до отказа записи ready, повтор проверяет
его вместо нового внешнего действия. Upload сначала проверяет прежний объект S3, даже если локальный файл утрачен;
совпавшие download/publication IDs, размер и SHA-256 metadata позволяют перейти к очистке без нового Complete. Перед
новым сеансом и при отмене удаляются только multipart uploads точного уникального ключа своей публикации, включая
сеансы с потерянным uploadId. После аварийного завершения известный файл `uploads/<publication-id>/part` удаляется
перед повторной отправкой, при отмене и при согласовании уже опубликованного объекта перед CleaningUp. Очистка относится
только к UUID этой публикации; каталоги других заданий и ручных отправок сохраняются. Несовпадающий объект не
перезаписывается и не удаляется.

Ответ own DELETE `202` подтверждает только очередь, а не отсутствие файла. Объект остается в каталоге до фактического
удаления и подтверждения отсутствия; повторный запрос pending DELETE идемпотентен. Обработка использует ту же single-worker
очередь, что и Download/Prepare/Upload/Cleanup. S3 metadata conflict или недоступность после принятия дают Failed с
ResumeState=Deleting и кодом `publication_changed`/`storage_unavailable`; повтор выполняет только оставшееся удаление.
Успех Cleanup/Delete/Cancel до сбоя БД согласуется по отсутствию оставшихся файлов/объекта. Запрет отмены CleaningUp
сохраняется в Failed/Queued; Uploading→CleaningUp и cancel защищены одной Version-проверкой.

Если ОС или незавершенный redirected I/O не позволяют подтвердить остановку за 5 секунд, worker сохраняет
`process_stop_unconfirmed`, оставляет файлы и прекращает claim, запрашивая остановку приложения даже при отказе БД.
Принятая пользовательская отмена сохраняет ResumeState=Canceling; обычная остановка хоста не превращается в отмену.
Перед ручным повтором пересоздайте/перезапустите контейнер целиком, чтобы завершился его process namespace с потомками;
перезапуск одного WebHost внутри прежнего процесса этого не гарантирует. Не удаляйте рабочие файлы ошибочных заданий
до решения администратора; автоматической очистки по возрасту нет.
Очистка считает отсутствием только подтвержденный `FileNotFound`/`DirectoryNotFound`; отказ доступа и другие ошибки
ввода-вывода сохраняют Failed и возможность ручного повтора, а не завершают задание с оставшимися файлами.

## Потоковая библиотека и отправка файлов

`id` — канонический Base64url полного UTF-8 S3-ключа. Каталог не ограничен заданиями из БД; вложенные префиксы отображаются
плоско. Имя скачивания берется из последнего компонента ключа, без управляющих символов и путей. Ответ всегда
`application/octet-stream` с `attachment` и `nosniff`, без redirect на MinIO и без Range-докачки. Ошибка после начала
потока прерывает передачу; JSON в файл не дописывается. `404` означает подтвержденное отсутствие объекта в доступном
bucket. Отсутствующий bucket, `403`, сеть и timeout дают `503`, а не подтверждение удаления. Для своих публикаций UUID и
ключ никогда не переиспользуются; устаревший publication-id дает409 и не затрагивает новый ключ. If-Match передается
как дополнительная защита поддерживающему storage (412 превращается в409), но проверенные старые MinIO игнорируют его:
атомарность против внешнего writer, заменившего ТОТ ЖЕ ключ строго между HEAD и DELETE, не гарантируется.

В каталоге заголовок (`title`) имеет приоритет перед исходным именем (`name`). Администратор может открыть форму
редактирования у любого файла, увидеть исходное имя и сохранить или удалить заголовок. Пустая строка после удаления
краевых пробелов сбрасывает заголовок, как и `null`. Сервер принимает до 255 кодовых единиц UTF-16 без управляющих
символов. Заголовок хранится в PostgreSQL. Скачивание сохраняет прежнюю ссылку, байты и исходное имя файла.

Upload принимает ровно одну file-part `file`; поля и дополнительные части отклоняются. Cookie/роль и CSRF-header
проверяются до чтения тела. Форма читается `MultipartReader`, без `IFormFile` и буферизации целого файла в RAM. Одна часть
временно записывается в `/data/work/uploads/<случайный-id>/part`, передается в S3 и переиспользуется; буфер памяти —128KiB.
Для известного Content-Length размер части выбирается с учетом 10000 частей S3 и границ 5MiB–5GiB; framing входит в
верхнюю оценку. При неизвестной длине часть может занимать до 5GiB временного диска. Нужны доступное место на `/data`,
поддержка выбранного размера хранилищем и подходящие транспортные timeout. Продуктовый предел размера не вводится.

Ключ ручного файла — `uploads/<случайный-id>/<безопасное-имя>`, он не перезаписывает совпадающие имена. До проверки конца
всей формы Complete не вызывается. Успех возвращается после HEAD с размером и metadata собственного operation id;
потеря ответа Complete проверяется тем же способом без повторного Complete. Незавершенные parts и временные файлы
очищаются только для своей операции; потерянный ответ Initiate согласуется listing multipart по собственному ключу.
При недоступном S3 сервер не может гарантировать немедленный abort: bucket lifecycle для старых незавершенных multipart
настраивается владельцем хранилища. Production-приложение не создает bucket и не меняет policy.

Kestrel body limit отключается только для авторизованного upload после CSRF. На внешнем proxy также разрешите нужный
размер, например nginx `client_max_body_size 0`, `proxy_request_buffering off`, и настройте подходящие timeout. Это
технические параметры окружения, а не ограничения типов или размера продукта. SDK не повторяет внешние команды
автоматически и использует отмену запроса вместо фиксированного timeout полного S3-запроса.

## Интерфейс

Открытая история следует текущему download ID и обновляется вместе с карточкой. Для каждой попытки показаны причина,
состояние/статус, точка начала, даты постановки/начала/завершения, этапы, ошибки и использование прежнего результата.
Известный прогресс относится только к текущему этапу; `null` остается индикатором без процента. Номер прежней попытки
поясняет reuse, а внутренние UUID не загромождают экран. Технические подробности внешних инструментов раскрываются отдельно.

`continuationReasonCode` независимо хранит причину продолжения: `media_plan_missing`, `artifacts_unavailable` или
`publication_reconciliation`. Она локализуется как пояснение и сохраняется даже при следующем отказе той же попытки;
реальные `errorCode/error` отображаются отдельно.
Произвольный stderr не переводится и не удаляется. Новые собственные ошибки хранят локализуемый код без английской прозы.

Публичный каталог и закрытые задания опрашиваются раз в 5 секунд в видимой вкладке без перекрытия запросов; focus и
возврат вкладки обновляют данные сразу. Задания не запрашиваются анонимно. Подтверждение публикации/удаления в очереди
обновляет каталог, `202` не удаляет строку преждевременно. Поздние ответы отмененного polling и прежней сессии игнорируются;
сетевая ошибка оставляет последние подтвержденные данные с явным предупреждением. Диалог отмены учитывает актуальные
allowedActions даже после открытия. Формы блокируют повторную отправку и закрытие до ответа.

Тексты, состояния попыток/этапов, причины повтора и даты/числа используют выбранный EN/RU язык браузера. Тема следует
`prefers-color-scheme`, анимация учитывает `prefers-reduced-motion`. Диалоги имеют доступные заголовки, управляют фокусом
и прокручиваются в пределах экрана; история и административные формы доступны на узкой ширине.

## Тесты

После сборки:

```powershell
dotnet test tests/Ytdlp.Ui.Tests/Ytdlp.Ui.Tests.csproj -c Release --no-build
dotnet test tests/Ytdlp.Ui.IntegrationTests/Ytdlp.Ui.IntegrationTests.csproj -c Release --no-build --filter 'TestCategory!=Storage'
dotnet test tests/Ytdlp.Ui.IntegrationTests/Ytdlp.Ui.IntegrationTests.csproj -c Release --no-build --filter 'TestCategory=Storage'
npm --prefix src/Ytdlp.Ui.Client test
```

Автономные `.Tests` проверяют агрегат/план форматов и настоящий запуск вспомогательного .NET-процесса без Docker:
буквальные аргументы, одновременный вывод, ограничение диагностики, отмену дерева и callback/drain failures.
`Ytdlp.Ui.ProcessFixture` — отдельный executable helper без NUnit/test dependencies. Интеграционные тесты используют настоящий
TestServer и настоящую PostgreSQL с миграциями. Root `SetupGlobal` владеет одним PostgreSQL Testcontainer и временным
каталогом набора; `Tools/TestDatabase` — отдельной БД и файлами сценария. `CustomWebApplicationFactory` настраивает
Testing и key ring, `SetupBase` владеет фабрикой/клиентом и ее БД. Startup/restart tests явно владеют общей БД и ключами.
При недоступном Docker все интеграционные наборы завершаются ошибкой; автономные `.Tests` по-прежнему не требуют Docker.
Обычная фабрика явно отключает worker, чтобы API/auth tests проверяли очередь без фоновой гонки. `MediaWorkerTests`
включает настоящий BackgroundService, домен, PostgreSQL и MinIO; заменяется только граница внешних process tools.
Проверяются четыре этапа, прогресс/последовательность, отклонение источника и cover-art, copy/контейнеры, отмена,
отказы объединения/S3/очистки и ручной повтор с сохраненными файлами. Recovery tests вводят отказы между готовым
файлом/Complete/очисткой/удалением и записью БД, проверяют размер/SHA-256 и потерю файла во время очереди, обе очередности
cancel/upload, orphan multipart, новый publication/workspace и stop barrier при отказе сохранить ошибку.

Fake OIDC provider предоставляет discovery/JWKS, подписанный RSA ID token и UserInfo через backchannel настоящего
OpenIdConnectHandler. Проверяются state/correlation/nonce, PKCE verifier, client/redirect, подпись/issuer/audience/срок,
группы только в UserInfo и несовпадение sub. Также проверяются 12 часов без sliding, отзыв/restart, отказ БД, upgrade
чистая initial PostgreSQL migration без импорта старых tickets и CSRF всех команд. Тесты не обращаются к
пользовательскому Authentik. Проверки прежнего SQLite-upgrade относятся к истории этапов1–3, а не к текущей миграции.

Vitest проверяет SSO-навигацию, отсутствие парольной формы и browser tokens, общий CSRF-запрос, публичную библиотеку,
`401`/сетевые ошибки, EN/RU и поздний успешный create из прежней сессии, который не должен закрывать новую URL-форму.
Дополнительно проверяются живая история и reuse, нейтральное оформление причины продолжения рядом с реальной ошибкой,
null/известный прогресс, обновление каталога без закрытых запросов, hidden/focus polling и поздние ответы предыдущего
listing/входа, изменение allowedActions в открытом подтверждении и двойная отправка формы. PostgreSQL-тесты проверяют
миграцию причин с сохранением настоящих diagnostics и независимость continuation/failure в реальном worker.
Интеграционные тесты namespace `Storage` требуют работающий Docker Engine и сеть для закрепленных
тестовых образов; они запускают настоящий MinIO с динамическим портом, отдельными bucket и данными каждого теста.
Используется готовый `bitnamilegacy/minio:2025.7.23-debian-12-r5` по digest
`sha256:6dabb4a2088c9a79908de3bc05f4586c23ad2182c8908e7e3acbf61c1467fb20`, без компиляции MinIO и без пользовательского
сервера. `MINIO_TEST_IMAGE` позволяет проверить другой готовый совместимый image, включая локальный
`minio/minio:RELEASE.2025-04-22T22-12-26Z`: нужны `minio` в PATH и writable `/tmp/minio-data`. Иначе default image
подтягивается из registry; закрепленные версии защищают воспроизводимость. Отдельный diagnostic test фиксирует
поддержку/игнорирование If-Match, не обещая ее для старого MinIO.
Отсутствие Docker — ошибка, а не skip. Прочие HTTP/OIDC/PostgreSQL-тесты заменяют только внешний storage boundary и
не запускают MinIO; их PostgreSQL также требует Docker. Default PostgreSQL image — готовый `postgres:18.6-alpine`
по digest `sha256:10a5c69a83035ab5239f5620d708520e8b28f5aa1f2b20143e6024532edadfb5`, с динамическим портом и удалением
тестовых БД/контейнера после набора. Пользовательские БД и S3 bucket не используются.

## Публикация и контейнер

`compose.yaml` подключает приложение к существующим MinIO и Authentik через переменные окружения. Для вспомогательного
локального MinIO: `docker compose -f compose.yaml -f compose.minio.yaml up --build`. Готовый закрепленный image MinIO
сохраняет данные в отдельном volume; его штатный init создает `ytdlp-ui` и публичную download policy только в этом
локальном opt-in варианте. Учетные данные задаются `LOCAL_MINIO_ROOT_USER`/`LOCAL_MINIO_ROOT_PASSWORD`; порты19000/19001.
Приложение не создает bucket и не меняет настройки существующего хранилища. Реальный Authentik остается внешним.

Для готового локального PostgreSQL добавьте `-f compose.postgresql.yaml`: например
`docker compose -f compose.yaml -f compose.minio.yaml -f compose.postgresql.yaml up --build`. PostgreSQL18.6 закреплен
по digest, порт15432, отдельный volume `/var/lib/postgresql` и пароль `LOCAL_POSTGRES_PASSWORD`; для основного внешнего
сервера используется `APP_DATABASE_CONNECTION`. Секреты не коммитятся. Учетная запись приложения должна иметь права
на миграции его схемы. Прежний SQLite volume можно сохранить как резервную копию; приложению нужны ключи и work.

```powershell
dotnet publish src/Ytdlp.Ui/Ytdlp.Ui.csproj -c Release --no-restore -o artifacts/publish
dotnet publish src/Ytdlp.Ui/Ytdlp.Ui.csproj -c Release --no-build -o artifacts/publish-no-build
```

Publish требует готовый `dist/index.html`; `--no-build` не компилирует React даже с BuildFrontend=true. Результат
содержит UI в wwwroot, без исходников клиента/node_modules. Совместный вариант —
`dotnet publish src/Ytdlp.Ui/Ytdlp.Ui.csproj -c Release -p:BuildFrontend=true -o artifacts/publish`.

```powershell
docker build -t ytdlp-ui:local .
docker run --rm -p 5080:8080 -v ytdlp-data:/data --env-file .env ytdlp-ui:local
```

`.env` содержит строку подключения PostgreSQL и OIDC/S3-параметры из таблицы выше; его нельзя коммитить с секретами. Volume `/data` сохраняет
`/data/keys` и `/data/work`; каталог принадлежит непривилегированному пользователю образа. PostgreSQL — внешний сервис,
его daemon не входит в приложение. Для замены контейнера сначала
останавливайте прежний экземпляр. Потеря key ring делает прежние cookie/tickets непригодными; восстановление требует
нового входа. Приложение работает под UID 1654; новые тома получают доступные этому пользователю keys/work. При bind mount
права каталогов нужно подготовить на стороне оператора.

Dockerfile собирает UI и .NET SDK 10.0.403 на архитектуре сборщика и публикует .NET assets для `TARGETARCH`.
Финальный ASP.NET 10.0.12 основан на Ubuntu 24.04; Node копируется из образа целевой архитектуры, FFmpeg и Python
устанавливаются готовыми пакетами Ubuntu. `build/media-requirements.txt` фиксирует версии и SHA-256 готовых Python wheels
для AMD64/ARM64. `pip --only-binary=:all: --require-hashes` исключает сборку внешних инструментов из исходников.
Обновление yt-dlp/EJS выполняется изменением lock-файла и пересборкой; при старте автообновлений нет.

`/health/live` не обращается к БД/S3/OIDC и проверяет ответ HTTP-процесса. `/health/ready` выполняет `SELECT 1` через
собственное соединение PostgreSQL без pooling с отдельными короткими probe-таймаутами,
создание и удаление одного собственного пробного файла в каждом work/keys и listing bucket с `MaxKeys=1`.
Внешний deadline readiness — 3 секунды, независимо от задержки отмены драйвера. Одна незавершенная проверка владеет
своими ресурсами до завершения; следующие запросы получают `503` без запуска новых проверок. Настройки БД заданий не меняются.
Ответы `Healthy`/`Unhealthy` не содержат адресов, ошибок и секретов и имеют
`Cache-Control: no-store`. Проверка не создает bucket, не запускает медиапроцессы и не делает запросов к Authentik.
Docker HEALTHCHECK вызывает readiness на внутреннем порту 8080 каждые 30 секунд (start period 30 секунд, timeout 6 секунд,
3 неудачные попытки). При переопределении порта нужно согласованно изменить probe. Недоступная PostgreSQL или ошибка
инициализации локальных каталогов прерывает startup. Недоступный S3 дает `ready=503`; процесс может оставаться live.

Основной `docker compose up --build` использует тот же volume и переменные OIDC/S3 из окружения; `WORKER_ENABLED=false`
при необходимости сохраняет API/очередь без claim новых попыток. Автоматический деплой в pipeline не входит. CI отдельно
строит React/.NET, запускает автономные и все интеграционные тесты с настоящими PostgreSQL/MinIO Testcontainers и проверяет
оба publish и побайтовое совпадение `wwwroot` с `dist`. Отсутствие Docker не пропускает контейнерные тесты.
Только после успешного job `build` job `docker` собирает общий manifest AMD64/ARM64. Вход в реестр Gitea и публикация разрешены только для
push в фактическую основную ветку `master`; PR собирается без входа в реестр и публикации. Имя owner приводится к нижнему
регистру. Для публикации job `docker` использует Actions secret `PACKAGES_TOKEN` с правом `write:package`. CI не запускает production-приложение и smoke-тесты образов.

Календарный тег имеет вид `YYYY.M.D.<github.run_number>`. Источник даты — прежняя author date `%ad`, преобразованная
в `Europe/Moscow` через `git show --date=format-local:`; committer date источник не заменяет. Регрессия
`bash build/test-calendar-version.sh` создает временный Git-репозиторий и проверяет границу 21:00 UTC с разными author/
committer offsets. Скрипт не изменяет Git-историю проекта и выполняется до упаковки в CI.

Переход на PostgreSQL выполняется с чистой БД по выбору пользователя: импорт истории и tickets SQLite не выполняется.
Старые SQLite-файлы приложение не открывает и не удаляет; сохраните их отдельно для отката прежнего образа. Подготовьте
PostgreSQL/учетную запись, задайте новую строку подключения: InitialPostgreSql применяется до приема запросов.
Единая начальная миграция рассчитана на пустую PostgreSQL. Для базы, созданной прежней цепочкой, перед обновлением
нужна отдельная сверка схемы и истории миграций.
Недоступная БД прерывает startup без локального fallback. Понадобится новый SSO-вход; S3-файлы остаются в библиотеке.
Резервные копии актуальных данных делайте средствами PostgreSQL (`pg_dump`/`pg_restore`) вместе с ключами/рабочими
файлами. Автоматического обратного импорта при откате и очистки S3 нет.

История этапа OIDC: локально проверены сборка и запуск `linux/amd64`: UI/assets, публичная библиотека, закрытие
административных API и прежнего парольного входа, SQLite/key ring в volume и сохранение ключей после замены контейнера.
Закрытые ключи имеют mode 600 и принадлежат UID 1654. Результаты независимого ревью и проверок —
в [журнале](openspec/review-log.md).

На этапе S3 локально собран и запущен образ `linux/amd64` с отдельным готовым MinIO. Через настоящий Kestrel проверены
multipart-отправка 34 MiB, анонимное потоковое скачивание с совпадающим SHA-256 и Content-Length, удаление с последующим
`404`, сохранность внешнего объекта и очистка собственного временного файла. Для этой транспортной проверки сессия
подготовлена через настоящий server ticket store; это не проверка входа в пользовательский Authentik.

На этапе PostgreSQL локальный `linux/amd64` запуск с отдельными PostgreSQL и MinIO подтвердил чистую миграцию,
постановку в очередь и отклонение дубликата, сохранение задания/истории и cookie при замене приложения, а также отзыв
сессии после следующего перезапуска. Публичный каталог доступен, история без входа возвращает `401`; контрольный прежний
SQLite-файл остался неизменным. Сессия подготовлена через настоящий ticket store для транспортной проверки.

На этапе медиапроцессов изолированный Linux AMD64 запуск с настоящими yt-dlp/FFmpeg/FFprobe и локальным HTTP-источником
подтвердил прямой MP4 с неизвестными metadata codecs/height и DASH с раздельными дорожками. Оба задания последовательно
достигли Completed, сохранили четыре успешных этапа в PostgreSQL, разные публикации S3 и очистили свои workspace.
Анонимно скачанные результаты проверены настоящим FFprobe: видео и аудио, 180p. Эта историческая проверка использовала
отдельно подготовленное окружение инструментов; состав финального Dockerfile описан выше. Произвольные внешние видеосайты и
пользовательский Authentik в этом сценарии не использовались.

На этапе восстановления тот же изолированный Linux запуск дополнительно проверил MKV с H264+Opus, отмену медленного
HTTP-скачивания и выполнение следующего задания. После SIGKILL и пересоздания приложения с прежними PG/work/keys
прерванное задание осталось Failed до ручного повтора, cookie сохранилась, ожидающее задание выполнилось. Проверены
own DELETE202→Deleted/404, повтор Deleted с новым publication ID, неизменный404 прежнего ключа и пустой work после успеха.

На этапе UI проверено обновление прежней PostgreSQL с сохранением cookie, заданий и истории. В браузере подтверждены
отправка файла 51 Б со сложным именем и анонимное скачивание с совпадающим SHA-256, отправка пустого файла, живая история
нового задания до Completed с четырьмя этапами и обновление каталога без ручного Refresh. EN/RU, формы, история,
ошибки и подтверждения проверены на десктопе и ширине 390 px без горизонтального переполнения. Тёмная тема проверена
при фактическом предпочтении браузера; базовые светлые стили просмотрены через отдельный тестовый proxy. Настоящее
переключение к native light preference в IAB не проверено. Production CSS и настройки пользователя не менялись.
Скриншоты и подробные результаты описаны в журнале ревью; временное тестовое окружение освобождено.

Для подключения по паролю в окружении без Kerberos можно задать `GSS Encryption Mode=Disable` в строке подключения:
это исключает попытку GSSAPI и сообщение об отсутствующей библиотеке; SSL/TLS настраивается отдельно.
См. [описание Npgsql 10](https://www.npgsql.org/doc/release-notes/10.0.html).

Финальный production-образ проверен отдельно на AMD64 и ARM64 под QEMU: native Node/yt-dlp/EJS/FFmpeg/FFprobe,
непривилегированный UID1654, реальные MP4/раздельные дорожки/MKV, отмена, публикация в S3 и локальная очистка.
Окончательные закрепленные образы подтвердили crash/restart, ручной retry с прежними cookie/ID/историей, queued DELETE202
с последующим404 и новый publication ID после повтора Deleted. На ARM предыдущий срез подтвердил DASH/MKV и активную
отмену; финальный — MP4, crash/retry/delete и проверки готовности. Ранний timeout ожидания старта под QEMU отделен от
успешного повтора с закрепленным image ID; старт и перезапуск эмулированного приложения заняли около 75/73 секунд.

На обоих финальных образах проверены live/ready/404 и pause/unpause отдельных MinIO/PostgreSQL: readiness дала 503
примерно за 3/2 секунды, liveness осталась 200, после восстановления зависимостей readiness вернулась 200. Прогретые
PostgreSQL pool имели 14/17 свободных физических соединений перед pause. Общий OCI index содержит AMD64 и ARM64;
сборка, отдельные запуски и проверки описаны раздельно в журнале ревью. Временное окружение и Buildx builder удалены.

Физический ARM64, реальный пользовательский Authentik и произвольные внешние видеосайты не проверялись.
Изолированные OIDC-тесты проверяют протокол через настоящий middleware, а не настройку конкретного сервера. Реальный
выбор светлой темы по предпочтению браузера также остается непроверенным; базовые светлые стили проверены отдельно.
Workflow проверен локально, удаленный CI и публикация в реестр Gitea не запускались; автоматического деплоя нет.

После завершения приемки пользователь обновил версии пакетов в Directory.Packages.props и прямо указал включить их в коммит без проверки. Сборка, тесты, publish и runtime-доказательства выше относятся к состоянию до этого обновления; новые версии пакетов повторно не проверялись.
