# yt-dlp WebUI

ASP.NET Core 10 Minimal API и React составляют одно приложение: Kestrel раздает API и собранный UI. Требования целевого
MVP находятся в [OpenSpec](openspec/README.md), согласованный переход на Authentik и отправку файлов —
в [активном change](openspec/changes/authentik-sso-and-file-uploads/proposal.md).

Реализован вход через Authentik по OIDC, административный доступ по группе, сохраняемые серверные cookie-сессии и
CSRF-защита команд. PostgreSQL мигрирует до приема запросов; сессии, их истечение и отзыв сохраняются при
перезапуске с прежними БД и ключами Data Protection.

Библиотека читает все объекты настроенного S3 bucket, включая внешние файлы и все страницы listing. Администратор может
отправить произвольный локальный файл; посетитель скачивает точные байты через бэкенд. Одинаковые имена получают разные
ключи. Удаление внешних объектов и собственных сохраненных публикаций работает синхронно, с проверкой состояния
агрегата и сохранением истории. Очередь повторного удаления и восстановление после рестарта появятся на следующих этапах.

API заданий сохраняет URL, попытки, этапы и команды в PostgreSQL; очередь переживает перезапуск. Конкурентные команды
защищены уникальностью и Version, а claim обеспечивает одну исполняемую попытку. Worker пока не подключен: новые
задания остаются Queued, отмена сохраняет Canceling/cleanup intent до подключения реальной обработки. Медиапроцессы и
полное согласование после сбоев относятся к следующим этапам.
Прогресс не симулируется. Интерфейс поддерживает телефон/десктоп, системную тему и EN/RU по браузеру.

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
    Features/{Auth,Downloads,Library,Hello}/
    Domain/
    Infrastructure/{Authentication,Persistence,Storage}/
    Configuration/
  Ytdlp.Ui.Client/
    package.json
    package-lock.json
    src/
    dist/  # результат сборки, не коммитится
tests/
  Directory.Build.props
  Ytdlp.Ui.Tests/             # доменные unit-тесты
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

| Метод и маршрут                   | Доступ и запрос                                                                   | Ответ                                                                                                                           |
|-----------------------------------|-----------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------|
| `GET /api/auth/login?returnUrl=/` | Навигация браузера; returnUrl только локальный                                    | OIDC redirect в Authentik; `400` для внешнего returnUrl                                                                         |
| `/signin-oidc`                    | Callback стандартного OIDC middleware                                             | После проверки — возврат в библиотеку; безопасный `authError=access_denied` или `login_failed` при отказе, без новых полномочий |
| `GET /api/auth/session`           | Публичный, cookie опциональна                                                     | `200 { authenticated, sessionId }`; без действующей административной сессии false/null                                          |
| `GET /api/auth/csrf`              | Администратор, cookie                                                             | `200 { requestToken }` для X-CSRF-TOKEN                                                                                         |
| `POST /api/auth/logout`           | Администратор, cookie + CSRF                                                      | `204` после сохраненного отзыва                                                                                                 |
| `GET /api/library`                | Публичный                                                                         | Все страницы S3 как плоский массив `{ id, name, size, lastModified, contentUrl }`                                               |
| `GET /api/library/{id}/content`   | Публичный                                                                         | Поток S3 с attachment, octet-stream, Content-Length и nosniff; `404` при подтвержденном отсутствии                              |
| `DELETE /api/library/{id}`        | Администратор, cookie + CSRF                                                      | `204`, включая отсутствующий объект; `409 publication_changed` при конфликте состояния/публикации; `503 storage_unavailable`    |
| `POST /api/library/uploads`       | Администратор, cookie + CSRF; multipart/form-data с единственной file-part `file` | `201` с `{ id, name, size, lastModified, contentUrl }` после проверки S3; `400 invalid_upload` или `503 storage_unavailable`    |
| `GET /api/downloads`              | Администратор                                                                     | Сохраняемые задания с попытками, этапами и allowedActions                                                                       |
| `GET /api/downloads/{id}`         | Администратор                                                                     | Задание или `404`                                                                                                               |
| `POST /api/downloads`             | Администратор, cookie + CSRF; `{ url }`                                           | `201` с Location; `202` для повтора удаленного результата; `400 invalid_url`, `409 download_already_exists`                     |
| `POST /api/downloads/{id}/retry`  | Администратор, cookie + CSRF                                                      | `202`, новая попытка; `404` или `409 invalid_state`                                                                             |
| `POST /api/downloads/{id}/cancel` | Администратор, cookie + CSRF                                                      | `202`; `404` или `409 invalid_state`                                                                                            |
| `GET /api/hello`                  | Публичный                                                                         | `200 text/plain`, `Hello World`                                                                                                 |

Административный API возвращает `401` без сессии и `403` без прав, без HTML-перенаправления. CSRF проверяется до
побочных действий; неверное подтверждение дает `400 antiforgery_failed`. Auth-ответы не кешируются. Неперехваченная
ошибка — `500 application/problem+json` без текста исключения и секретов. Неизвестный `/api/*` возвращает `404` без
React HTML; отсутствующий static asset — `404`, маршруты UI без расширения поддерживают SPA fallback.

Состояние задания сохраняет прежние поля интерфейса:
`{ id, url, title, state, resumeState, progress, createdAt, errorCode, error, allowedActions, attempts }`;
добавлены `currentAttemptId`, `publicationId`, `publicationState`, `failedState`, `cancelRequested`, `version`.
Попытка содержит `{ number, state, createdAt, error, id, reason, status, initialState, startedAt, finishedAt, errorCode, stages }`;
этап — `{ id, stage, status, startedAt, finishedAt, progress, errorCode, error, artifactId, reusedFromStageId }`.
Прогресс берется только из фактической записи выполняемого этапа; до подключения worker он `null`.

Исходный URL хранится точно как принят, отдельно от нормализованного. Удаляются только внешние пробелы, нормализуются
scheme/host/default port; raw path, query/order и fragment сохраняются. Полный URL остается text; уникальный ключ —
SHA-256 normalized UTF-8 из32bytes, вычисляемый persistence при каждой записи. После lookup сравнивается точная строка:
теоретический hash conflict дает `409 url_conflict` без чужого ID. Дубликат дает `409 download_already_exists` и
`existingDownloadId`. Для Deleted и подтвержденного S3-отсутствия Completed результатом становится новая попытка того
же ID с новым publication/workspace UUID. Failed/Canceled требуют явного retry. Ответы201/202 включают Location.

Queue claim сериализуется коротким PostgreSQL advisory transaction lock, проверяет отсутствие исполняемой попытки и
выбирает FIFO по QueuedAt/Id; сеть/процессы внутри транзакции не выполняются. Optimistic Version и unique active-attempt
откатывают устаревшие команды вместе с историей/intent. Отмена идемпотентна и не стирает cleanup intent. CleaningUp и
retry очистки не допускают отмену. Реальное выполнение и restart recovery будут подключены следующими этапами.

## Потоковая библиотека и отправка файлов

`id` — канонический Base64url полного UTF-8 S3-ключа. Каталог не ограничен заданиями из БД; вложенные префиксы отображаются
плоско. Имя скачивания берется из последнего компонента ключа, без управляющих символов и путей. Ответ всегда
`application/octet-stream` с `attachment` и `nosniff`, без redirect на MinIO и без Range-докачки. Ошибка после начала
потока прерывает передачу; JSON в файл не дописывается. `404` означает подтвержденное отсутствие объекта в доступном
bucket. Отсутствующий bucket, `403`, сеть и timeout дают `503`, а не подтверждение удаления. Для своих публикаций UUID и
ключ никогда не переиспользуются; устаревший publication-id дает409 и не затрагивает новый ключ. If-Match передается
как дополнительная защита поддерживающему storage (412 превращается в409), но проверенные старые MinIO игнорируют его:
атомарность против внешнего writer, заменившего ТОТ ЖЕ ключ строго между HEAD и DELETE, не гарантируется.

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

## Тесты

После сборки:

```powershell
dotnet test tests/Ytdlp.Ui.Tests/Ytdlp.Ui.Tests.csproj -c Release --no-build
dotnet test tests/Ytdlp.Ui.IntegrationTests/Ytdlp.Ui.IntegrationTests.csproj -c Release --no-build --filter 'TestCategory!=Storage'
dotnet test tests/Ytdlp.Ui.IntegrationTests/Ytdlp.Ui.IntegrationTests.csproj -c Release --no-build --filter 'TestCategory=Storage'
npm --prefix src/Ytdlp.Ui.Client test
```

Unit-тесты проверяют агрегат и переходы автомата без хоста и Docker. Интеграционные тесты используют настоящий
TestServer и настоящую PostgreSQL с миграциями. Root `SetupGlobal` владеет одним PostgreSQL Testcontainer и временным
каталогом набора; `Tools/TestDatabase` — отдельной БД и файлами сценария. `CustomWebApplicationFactory` настраивает
Testing и key ring, `SetupBase` владеет фабрикой/клиентом и ее БД. Startup/restart tests явно владеют общей БД и ключами.
При недоступном Docker все интеграционные наборы завершаются ошибкой; unit по-прежнему не требуют Docker.

Fake OIDC provider предоставляет discovery/JWKS, подписанный RSA ID token и UserInfo через backchannel настоящего
OpenIdConnectHandler. Проверяются state/correlation/nonce, PKCE verifier, client/redirect, подпись/issuer/audience/срок,
группы только в UserInfo и несовпадение sub. Также проверяются 12 часов без sliding, отзыв/restart, отказ БД, upgrade
чистая initial PostgreSQL migration без импорта старых tickets и CSRF всех команд. Тесты не обращаются к
пользовательскому Authentik. Проверки прежнего SQLite-upgrade относятся к истории этапов1–3, а не к текущей миграции.

Vitest проверяет SSO-навигацию, отсутствие парольной формы и browser tokens, общий CSRF-запрос, публичную библиотеку,
`401`/сетевые ошибки, EN/RU и поздний успешный create из прежней сессии, который не должен закрывать новую URL-форму.
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
нового входа. Текущий образ еще не содержит готовую медиаподсистему.

Основной `docker compose up --build` использует тот же volume и переменные OIDC/S3 из окружения. Подключение
FFmpeg/yt-dlp описывается по мере реализации; автоматический деплой в pipeline не входит. CI отдельно
строит React/.NET, запускает unit и интеграционные тесты и проверяет оба publish; job docker строит AMD64/ARM64 и
публикует GHCR только из основной ветки. CI не запускает production-приложение и smoke-тесты образов.

Переход на PostgreSQL выполняется с чистой БД по выбору пользователя: импорт истории и tickets SQLite не выполняется.
Старые SQLite-файлы приложение не открывает и не удаляет; сохраните их отдельно для отката прежнего образа. Подготовьте
PostgreSQL/учетную запись, задайте новую строку подключения: InitialPostgreSql применяется до приема запросов.
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

Для подключения по паролю в окружении без Kerberos можно задать `GSS Encryption Mode=Disable` в строке подключения:
это исключает попытку GSSAPI и сообщение об отсутствующей библиотеке; SSL/TLS настраивается отдельно.
См. [описание Npgsql 10](https://www.npgsql.org/doc/release-notes/10.0.html).

Реальный вход в пользовательский Authentik и запуск ARM64 требуют отдельного подходящего окружения. Пройденные
изолированные OIDC-тесты не являются проверкой настройки конкретного сервера.
