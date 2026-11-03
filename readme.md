# yt-dlp WebUI

ASP.NET Core 10 Minimal API и React составляют одно приложение: Kestrel раздает API и собранный UI. Требования целевого
MVP находятся в [OpenSpec](openspec/README.md), согласованный переход на Authentik и отправку файлов —
в [активном change](openspec/changes/authentik-sso-and-file-uploads/proposal.md).

Реализован вход через Authentik по OIDC, административный доступ по группе, сохраняемые серверные cookie-сессии и
CSRF-защита команд. SQLite создается и мигрирует до приема запросов; сессии, их истечение и отзыв сохраняются при
перезапуске с прежними БД и ключами Data Protection.

Задания и каталог пока демонстрационные: доступны интерфейс очереди, создание по URL, повтор, отмена и удаление трех
текстовых файлов. Изменения этих данных сбрасываются при перезапуске. Агрегат задания и постоянная схема истории уже
существуют, но подключение API к ним, MinIO, ручная отправка файлов и реальные медиапроцессы выполняются в следующих
этапах. Прогресс не симулируется. Интерфейс поддерживает телефон/десктоп, системную тему и EN/RU по браузеру.

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
    Infrastructure/{Authentication,Persistence,Mocking}/
    Configuration/
  Ytdlp.Ui.Client/
    package.json
    package-lock.json
    src/
    dist/  # результат сборки, не коммитится
tests/
  Directory.Build.props
  Ytdlp.Ui.Tests/             # доменные unit-тесты
  Ytdlp.Ui.IntegrationTests/  # HTTP, OIDC и файловая SQLite
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

| Параметр                             | Назначение                                                                                 |
|--------------------------------------|--------------------------------------------------------------------------------------------|
| `Oidc__Authority`                    | HTTPS issuer приложения, например `https://authentik.example.test/application/o/ytdlp-ui/` |
| `Oidc__ClientId`                     | Идентификатор confidential client                                                          |
| `Oidc__ClientSecret`                 | Секрет клиента только на бэкенде                                                           |
| `Oidc__GroupsClaim`                  | Claim с группами, обычно `groups`                                                          |
| `Oidc__AdminGroup`                   | Группа администраторов, например `ytdlp-ui-admins`                                         |
| `ConnectionStrings__AppDatabase`     | Файловая SQLite; в контейнере `Data Source=/data/db/app.db`                                |
| `Paths__DataProtectionKeysDirectory` | Постоянный каталог ключей; в контейнере `/data/keys`                                       |
| `ReverseProxy__KnownProxies__0`      | Доверенный IP reverse proxy; дополнительные адреса задаются индексами                      |
| `ReverseProxy__KnownNetworks__0`     | Доверенная CIDR-сеть вместо отдельных адресов при необходимости                            |
| `ReverseProxy__AllowedHosts__0`      | Разрешенный внешний host из X-Forwarded-Host                                               |

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
$env:ConnectionStrings__AppDatabase = 'Data Source=' + (Join-Path (Get-Location) 'data/db/app.db')
$env:Paths__DataProtectionKeysDirectory = Join-Path (Get-Location) 'data/keys'
dotnet run --project src/Ytdlp.Ui -c Release --no-build
```

Профиль разработки слушает `http://localhost:5080`; для локального OIDC настройте подходящий callback/proxy в своей
среде. В production callback — HTTPS. Для React разработки `npm --prefix src/Ytdlp.Ui.Client run dev` использует Vite
proxy к бэкенду на 5080; рекомендуемый SSO-сценарий — единый origin через Kestrel/reverse proxy.

## API

| Метод и маршрут                   | Доступ и запрос                                | Ответ                                                                                                                           |
|-----------------------------------|------------------------------------------------|---------------------------------------------------------------------------------------------------------------------------------|
| `GET /api/auth/login?returnUrl=/` | Навигация браузера; returnUrl только локальный | OIDC redirect в Authentik; `400` для внешнего returnUrl                                                                         |
| `/signin-oidc`                    | Callback стандартного OIDC middleware          | После проверки — возврат в библиотеку; безопасный `authError=access_denied` или `login_failed` при отказе, без новых полномочий |
| `GET /api/auth/session`           | Публичный, cookie опциональна                  | `200 { authenticated, sessionId }`; без действующей административной сессии false/null                                          |
| `GET /api/auth/csrf`              | Администратор, cookie                          | `200 { requestToken }` для X-CSRF-TOKEN                                                                                         |
| `POST /api/auth/logout`           | Администратор, cookie + CSRF                   | `204` после сохраненного отзыва                                                                                                 |
| `GET /api/library`                | Публичный                                      | Полный демонстрационный массив `{ id, name, size, lastModified, contentUrl }`                                                   |
| `GET /api/library/{id}/content`   | Публичный                                      | Поток демонстрационного файла с attachment; `404` при отсутствии                                                                |
| `DELETE /api/library/{id}`        | Администратор, cookie + CSRF                   | `204`, включая отсутствующий файл; `409 invalid_state` для незавершенной очистки связанного задания                             |
| `GET /api/downloads`              | Администратор                                  | Массив демонстрационных заданий                                                                                                 |
| `GET /api/downloads/{id}`         | Администратор                                  | Задание или `404`                                                                                                               |
| `POST /api/downloads`             | Администратор, cookie + CSRF; `{ url }`        | `201` с Location; `202` для повтора удаленного результата; `400 invalid_url`, `409 download_already_exists`                     |
| `POST /api/downloads/{id}/retry`  | Администратор, cookie + CSRF                   | `202`, новая попытка; `404` или `409 invalid_state`                                                                             |
| `POST /api/downloads/{id}/cancel` | Администратор, cookie + CSRF                   | `202`; `404` или `409 invalid_state`                                                                                            |
| `GET /api/hello`                  | Публичный                                      | `200 text/plain`, `Hello World`                                                                                                 |

Административный API возвращает `401` без сессии и `403` без прав, без HTML-перенаправления. CSRF проверяется до
побочных действий; неверное подтверждение дает `400 antiforgery_failed`. Auth-ответы не кешируются. Неперехваченная
ошибка — `500 application/problem+json` без текста исключения и секретов. Неизвестный `/api/*` возвращает `404` без
React HTML; отсутствующий static asset — `404`, маршруты UI без расширения поддерживают SPA fallback.

Состояние мокового задания:
`{ id, url, title, state, resumeState, progress, createdAt, errorCode, error, allowedActions, attempts }`;
`progress=null`, история `{ number, state, createdAt, error }`. Полная история этапов и работа worker еще не подключены
к API.

## Тесты

После сборки:

```powershell
dotnet test tests/Ytdlp.Ui.Tests/Ytdlp.Ui.Tests.csproj -c Release --no-build
dotnet test tests/Ytdlp.Ui.IntegrationTests/Ytdlp.Ui.IntegrationTests.csproj -c Release --no-build
npm --prefix src/Ytdlp.Ui.Client test
```

Unit-тесты проверяют агрегат и переходы автомата без хоста и Docker. Интеграционные тесты используют настоящий
TestServer и отдельную файловую SQLite с миграциями. `Tools/CustomWebApplicationFactory` настраивает Testing и временный
key ring; `SetupBase` владеет фабрикой/клиентом одного сценария, `SetupGlobal` — временным каталогом набора.
Startup/restart/options тесты владеют своими хостами; низкоуровневые ограничения SQLite проверяются отдельными
контекстами.

Fake OIDC provider предоставляет discovery/JWKS, подписанный RSA ID token и UserInfo через backchannel настоящего
OpenIdConnectHandler. Проверяются state/correlation/nonce, PKCE verifier, client/redirect, подпись/issuer/audience/срок,
группы только в UserInfo и несовпадение sub. Также проверяются 12 часов без sliding, отзыв/restart, отказ БД, upgrade
InitialPersistence без потери истории и CSRF всех команд. Тесты не обращаются к пользовательскому Authentik.

Vitest проверяет SSO-навигацию, отсутствие парольной формы и browser tokens, общий CSRF-запрос, публичную библиотеку,
`401`/сетевые ошибки, EN/RU и поздний успешный create из прежней сессии, который не должен закрывать новую URL-форму.
Реальная отправка файлов и Testcontainers MinIO будут добавлены следующим этапом; текущие интеграционные тесты Docker не
требуют.

## Публикация и контейнер

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

`.env` содержит реальные OIDC-параметры из таблицы выше; его нельзя коммитить с секретами. Volume `/data` сохраняет
SQLite и `/data/keys`; каталог принадлежит непривилегированному пользователю образа. Для замены контейнера сначала
останавливайте прежний экземпляр. Потеря key ring делает прежние cookie/tickets непригодными; восстановление требует
нового входа. Текущий образ еще не содержит готовую медиаподсистему.

Вспомогательный `docker compose up --build` использует тот же volume и переменные OIDC из окружения. Подключение
настоящего MinIO/FFmpeg/yt-dlp описывается по мере реализации; автоматический деплой в pipeline не входит. CI отдельно
строит React/.NET, запускает unit и интеграционные тесты и проверяет оба publish; job docker строит AMD64/ARM64 и
публикует GHCR только из основной ветки. CI не запускает production-приложение и smoke-тесты образов.

Новая миграция OidcSessions применяется автоматически при запуске, отзывает прежние локальные сессии и сохраняет
задания/историю. Перед развертыванием делайте резервную копию SQLite. Для отката нужен прежний образ с соответствующей
резервной копией БД; старые JWT/refresh не переносятся в OIDC-сессии.

Локально на этапе OIDC проверены сборка и запуск `linux/amd64`: UI/assets, публичная библиотека, закрытие
административных API и прежнего парольного входа, SQLite/key ring в volume и сохранение ключей после замены контейнера.
Закрытые ключи имеют mode 600 и принадлежат UID 1654. Результаты независимого ревью и проверок —
в [журнале](openspec/review-log.md).

Реальный вход в пользовательский Authentik и запуск ARM64 требуют отдельного подходящего окружения. Пройденные
изолированные OIDC-тесты не являются проверкой настройки конкретного сервера.
