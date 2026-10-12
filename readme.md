# yt-dlp WebUI

Приложение из [MVP-SPEC.md](MVP-SPEC.md): ASP.NET Core 10 Minimal API и React с отдельными каталогами и сборками. Kestrel раздает API и собранный интерфейс с одного адреса.

Сейчас реализованы настоящая авторизация и UI с моковыми заданиями и библиотекой: вход администратора, очередь и история загрузок, создание по URL, повтор, отмена и удаление файла. Интерфейс следует Material 3, адаптируется к телефону и десктопу, выбирает системную тему и язык EN/RU по браузеру. Форма входа открывается справа сверху на десктопе; административные функции появляются на той же странице после проверки входа.

Моковый API хранит данные заданий и библиотеки в памяти. В библиотеке находятся три небольших демонстрационных текстовых файла, которые действительно можно скачать. Очередь содержит примеры разных состояний, включая ошибку отправки и ошибку очистки. Создание и повтор ставят задания в очередь, но мок не выполняет реальные загрузки и не увеличивает прогресс по таймеру. После перезапуска изменения этих данных сбрасываются. Авторизация использует SQLite: вход, проверку активности сессии, обновление токенов и отзыв. Подключение остальных API к SQLite, MinIO, yt-dlp и FFmpeg относится к следующим этапам. Сейчас внешние сервисы не требуются.

Каталоги компонентов созданы по [TECHNICAL-DESIGN.md](TECHNICAL-DESIGN.md); пустые сохраняются в Git через `.gitkeep`.

Начата поэтапная реализация настоящего бэкенда. В первой итерации добавлен агрегат `Download` в `src/Ytdlp.Ui/Domain/`: он хранит состояние задания и применяет события через внутренний автомат. Свойства доступны только для чтения; переход выполняется через `Apply`, доступные действия вычисляются из текущего состояния. `Snapshot` возвращает неизменяемый снимок для хранения, `Restore` восстанавливает агрегат из сохраненного снимка. Недопустимое событие не меняет агрегат. Доменная модель не выполняет ввод-вывод. API пока использует прежний мок; подключение агрегата к SQLite, командам и worker выполняется в следующих итерациях.

Во второй итерации добавлены `AppDbContext` и отдельные EF-сущности для задания, попыток, этапов, артефактов, отправок в S3 и сессий администратора в `Infrastructure/Persistence/`. Конфигурации сущностей находятся в `AppDbContext.OnModelCreating`. Первая миграция создает схему SQLite. Уникальные индексы защищают нормализованный URL, номер попытки и единственную активную попытку задания. Версия записи задания служит concurrency token; EF отклоняет сохранение устаревшего состояния. `DownloadRecord` сохраняет снимок агрегата, намерение следующего действия и восстанавливает агрегат. Даты хранятся как Unix milliseconds, состояния — строками. `AddAppDatabase` напрямую настраивает `AppDbContext` через `AddDbContext` с конфигурацией хоста: foreign keys и ожидание блокировки до пяти секунд. Регистрация вызывается в `Program.cs`, собственных фабрик контекста нет.

При запуске `Program.cs` вызывает `AppDbContext.InitializeAsync` в отдельном scope до приема HTTP-запросов: создается каталог БД, включается WAL и применяются миграции. Повторный запуск сохраняет существующие данные и применяет только недостающие миграции. Ошибка доступа к каталогу, открытия БД или миграции прерывает запуск.

Успешный `POST /api/auth/login` сохраняет `AdminSessionRecord` через имеющийся `AppDbContext` до возврата токенов: `Id` совпадает с `jti` JWT, `IssuedAt` — время входа, `ExpiresAt` — абсолютное истечение сессии через 12 часов, `RevokedAt=null`. Access token действует 5 минут; `expiresAt` в JSON — его срок, совпадающий с JWT `exp`. Refresh token — случайные 32 байта в стандартном Base64 (44 символа с padding); в SQLite хранится только SHA-256 его UTF-8 строки в `RefreshTokenHash`: `byte[]?`, BLOB из 32 байт. Размер случайных данных определяется `SHA256.HashSize`, длина Base64 — стандартным API; refresh проверяет длину, формат и размер декодированных данных. Токен передается в JSON, включая символы `+`, `/` и `=`. Неверные учетные данные не создают запись; ошибка записи прерывает вход без токенов.

`POST /api/auth/refresh` принимает `{ refreshToken }` и атомарно заменяет его хеш, возвращая новую пару. При двух одновременных запросах с прежним refresh ровно один может получить `200`, другой получает `401`; старый токен больше не работает. Обновление не продлевает 12-часовую сессию. Access token, выданный перед ее истечением, ограничен оставшимся временем. Активность сессии и logout проверяются/сохраняются в SQLite, поэтому действующие токены переживают рестарт; выход сразу отзывает оба вида токенов. Запись с `RefreshTokenHash=null` не считается активной.

Авторизация находится в `Infrastructure/Authentication/`: scoped-сервис `AuthenticationService` получает `IOptions<AdminOptions>`, `IOptions<JwtOptions>`, `AppDbContext` и `TimeProvider` через DI и выполняет вход, refresh, проверку активности и отзыв. Типизированные настройки связаны с прежними секциями `Admin` и `Jwt`; выдача и проверка JWT используют одни options и `JwtOptions.GetSigningKeyBytes()` для декодирования ключа Base64. Отсутствующий, пустой, некорректный Base64 или ключ короче 32 декодированных байт прерывает запуск с сообщением без значения секрета. Сервис владеет экземпляром SHA-256 и освобождает его при завершении scope. `AuthenticationServiceCollectionExtensions.AddAdminAuthentication` регистрирует сервис, JWT Bearer и политику администратора. `TimeProvider.System` зарегистрирован singleton в `Program.cs`. HTTP-обработчики вызывают сервис, а JWT-handler получает его из scope запроса. Моковый `DemoStore` заданий и библиотеки зарегистрирован отдельно в `Program.cs`; он не участвует в авторизации.

## Структура

```text
Ytdlp.Ui.slnx
global.json
Directory.Build.props
Directory.Packages.props
NuGet.Config
Dockerfile
compose.yaml
.github/workflows/ci.yml
src/
  Directory.Build.props
  Ytdlp.Ui/
    Ytdlp.Ui.csproj
    Program.cs
    Features/Hello/
    Features/{Auth,Downloads,Library}/
    Domain/
      Download.cs
      DownloadStateMachine.cs
      Events/
      ValueObjects/
      Primitives/
    Processing/
    Infrastructure/{Persistence,Media,Storage,Authentication}/
    Infrastructure/Persistence/{Entities,Migrations}/
    Infrastructure/Mocking/
    Configuration/
  Ytdlp.Ui.Client/
    package.json
    package-lock.json
    src/{api,components,features,locales,styles,test}/
    dist/  # результат сборки, не коммитится
tests/
  Directory.Build.props
  Ytdlp.Ui.Tests/
```

## Сборка и запуск

Для сборки всего приложения требуются .NET SDK **10.0.401**, Node.js **24.19.0** и npm в `PATH`. Для отдельной сборки API Node.js/npm не нужны. SDK строго зафиксирован в `global.json`; CI и Docker используют ту же версию. NuGet-зависимости управляются через CPM; `NuGet.Config` ограничивает источники публичным `nuget.org`, чтобы сборка не зависела от пользовательских feeds. Зависимости React фиксируются в `src/Ytdlp.Ui.Client/package-lock.json`.

Из корня репозитория:

```powershell
npm --prefix src/Ytdlp.Ui.Client ci --no-audit --no-fund
npm --prefix src/Ytdlp.Ui.Client run build
dotnet restore Ytdlp.Ui.slnx
dotnet build Ytdlp.Ui.slnx -c Release --no-restore
$databasePath = Join-Path (Get-Location) 'data/db/app.db'
dotnet run --project src/Ytdlp.Ui -c Release --no-build -- --ConnectionStrings:AppDatabase "Data Source=$databasePath"
```

Открыть `http://localhost:5080/`. Профиль запуска использует локальный HTTP без настройки сертификатов.

По умолчанию `dotnet build` и `dotnet publish` не вызывают npm. React собирается отдельно: TypeScript проверяет типы, Vite создает `src/Ytdlp.Ui.Client/dist/`. MSBuild включает готовые файлы в `wwwroot` результата сборки и публикации; в каталог исходников бэкенда они не копируются. Сборка только API допускается без `dist`, но публикация требует готовый UI. После изменения React сначала пересоберите фронтенд, затем .NET.

Совместная сборка включается явно:

```powershell
dotnet build Ytdlp.Ui.slnx -c Release -p:BuildFrontend=true
```

В этом режиме MSBuild выполняет `npm ci`, если зависимости отсутствуют или изменился манифест/lock-файл, затем сборку React. Только фронтенд можно собрать через `dotnet build src/Ytdlp.Ui/Ytdlp.Ui.csproj -t:BuildFrontend`; это не компилирует бэкенд.

Для разработки React с обновлением страницы можно запустить `npm --prefix src/Ytdlp.Ui.Client run dev`. Vite проксирует `/api` на локальный бэкенд `http://localhost:5080`. При работе через Vite бэкенд можно собрать без UI.

## API

| Метод и маршрут | Авторизация | Запрос | Ответ |
| --- | --- | --- | --- |
| `GET /api/hello` | Не требуется | Без тела и параметров | `200`, `Content-Type: text/plain; charset=utf-8`, тело `Hello World` |
| `GET /api/library` | Не требуется | Без тела | `200`, полный массив `{ id, name, size, lastModified, contentUrl }` |
| `GET /api/library/{id}/content` | Не требуется | Без тела | `200`, поток демонстрационного файла с `Content-Disposition: attachment`; `404`, если отсутствует |
| `DELETE /api/library/{id}` | Администратор | Без тела | `204`, включая уже отсутствующий файл; `409 invalid_state` при незавершённой очистке связанного задания |
| `GET /api/auth/session` | Не требуется | Опциональный Bearer JWT | `200`, `{ authenticated }`; `401` при переданном недействительном токене |
| `POST /api/auth/login` | Не требуется | `{ username, password }` | `200` после сохранения сессии в SQLite, `{ accessToken, tokenType: "Bearer", expiresAt, refreshToken }`; `401` при неверных данных; `500` при ошибке записи, без токенов |
| `POST /api/auth/refresh` | Без access token | `{ refreshToken }` | `200`, новая пара в том же формате; `401` для неверного, истекшего, отозванного или уже использованного refresh; `500` при ошибке БД, без новой пары |
| `POST /api/auth/logout` | Администратор | Без тела | `204`, отзыв текущей сессии |
| `GET /api/downloads` | Администратор | Без тела | `200`, массив заданий с состояниями, действиями и моковой историей |
| `GET /api/downloads/{id}` | Администратор | Без тела | `200`, задание; `404`, если отсутствует |
| `POST /api/downloads` | Администратор | `{ url }` | `201` с `Location` для нового задания; `202` для повтора удалённого результата; `400 invalid_url`, `409 download_already_exists` |
| `POST /api/downloads/{id}/retry` | Администратор | Без тела | `202`, новая попытка с прежним ID задания; `404` либо `409 invalid_state` |
| `POST /api/downloads/{id}/cancel` | Администратор | Без тела | `202`, состояние Canceled; `404` либо `409 invalid_state` |

Административные запросы требуют `Authorization: Bearer <token>`; без входа возвращается `401`. JWT проверяется по подписи, HS256, issuer, audience, сроку и активности сессии в SQLite. Cookie и query-параметры не используются для авторизации; refresh передается только в JSON. Ответы авторизации имеют `Cache-Control: no-store`. Ошибки команд возвращаются как Problem Details с `code` для локализации в UI.

Моковое задание содержит `{ id, url, title, state, resumeState, progress, createdAt, errorCode, error, allowedActions, attempts }`. `progress` равен `null`, а индикатор активного этапа показывает неопределённый прогресс. История содержит `{ number, state, createdAt, error }`. Это предварительные DTO для работы над UI, без настоящей истории отдельных медиаэтапов.

Неизвестные пути `/api/*` возвращают `404` без HTML интерфейса. `GET /` возвращает страницу React; пути UI без расширения поддерживают SPA fallback. Отсутствующий статический ресурс возвращает `404`.

## Тесты без внешнего окружения

```powershell
dotnet test tests/Ytdlp.Ui.Tests/Ytdlp.Ui.Tests.csproj -c Release --no-build
```

После установки npm-зависимостей запустить UI-тесты из корня:

```powershell
npm --prefix src/Ytdlp.Ui.Client test
```

NUnit-тесты используют встроенный `WebApplicationFactory`/TestServer без отдельного процесса сервера и контейнеров. Общая `TestApplicationFactory` задает отдельную временную SQLite для каждого хоста, изолирует Data Protection от пользовательского каталога ключей и отключает системный EventLog в Windows. Проверяются контракт API, выдача файлов, ограничения доступа, отзыв JWT, запрет токенов в cookie/query, дубликаты URL, история после удаления и запрет отмены очистки до и после повтора. Проверки страницы React и доступности собранного JavaScript также сохранены. NSubstitute включен в общие зависимости тестов для будущих заглушек внешних компонентов.

Агрегат и его автомат проверяются отдельными unit-тестами без запуска приложения. Проверки охватывают переходы и отказы, подтверждение публикации/очистки/удаления, точки ручного повтора, прерывание операций, идентификаторы попыток и публикаций, идемпотентную отмену, `allowedActions` и оба порядка событий при гонке отмены с успехом Uploading. Дополнительно проверяются последовательные переходы одного агрегата, его восстановление и неизменность ранее полученных снимков.

SQLite-тесты используют отдельную временную файловую БД с настоящими миграциями и удаляют ее после каждой проверки. Проверяются повторное применение миграций, соответствие модели схеме, сохранение и повторное чтение всех сущностей, порядок очереди, уникальные индексы при одновременной записи, принадлежность артефактов и этапов заданию, запрет каскадного удаления истории, версия записи и откат конкурентного перехода вместе с историей. Отдельно проверяются транзакционное создание повтора и оба исхода гонки отмены с подтверждением Uploading. Проверки запуска хоста подтверждают создание БД в WAL до запросов, сохранение записи и истории миграций после перезапуска и отказ запуска при невозможности создать каталог БД. Эти тесты не требуют Docker.

Запустить только проверки доменного агрегата после Release-сборки:

```powershell
dotnet test tests/Ytdlp.Ui.Tests/Ytdlp.Ui.Tests.csproj -c Release --no-build --filter FullyQualifiedName~Ytdlp.Ui.Tests.Domain.DownloadTests
```

Авторизация проверяется через API на файловой SQLite: сроки access/сессии, хранение только хеша refresh как BLOB32, неверные учетные данные и параметры JWT, ошибка вставки/ротации без токенов, single-use ротация с единственным конкурентным победителем, отзыв обоих токенов и гонка logout с refresh. Управляемое время в тестах сервиса задает `FakeTimeProvider` из тестового пакета `Microsoft.Extensions.TimeProvider.Testing`: проверяются дробные секунды, абсолютная граница сессии и точное время отзыва. Проверки рестарта подтверждают действующую сессию и refresh после перезапуска. На свежей БД с единственной миграцией проверяются сравнение бинарных хешей по содержимому, уникальный индекс и допустимость нескольких null.

Только SQLite-проверки:

```powershell
dotnet test tests/Ytdlp.Ui.Tests/Ytdlp.Ui.Tests.csproj -c Release --no-build --filter TestCategory=SQLite
```

Vitest и Testing Library проверяют публичную библиотеку без запросов закрытых данных, восстановление истекшего access через refresh, вход/выход, валидацию URL, один повтор после `401`, общий refresh для параллельных запросов, окончательный `401` и сохранение пары при `500`. Проверяются поздние ответы после logout/нового login и независимость общего refresh от отмены отдельного запроса. Отдельно проверяются выбор языка браузера и ресурсы EN/RU.

Контейнерных интеграционных тестов на этой стадии нет. При реализации MinIO они будут добавлены в отдельный проект с Testcontainers и обязательным запуском в CI; локальные тесты без Docker останутся отдельным набором.

## Публикация

```powershell
dotnet publish src/Ytdlp.Ui/Ytdlp.Ui.csproj -c Release --no-restore -o artifacts/publish
dotnet publish src/Ytdlp.Ui/Ytdlp.Ui.csproj -c Release --no-build -o artifacts/publish-no-build
```

Обе команды используют предварительно собранный `src/Ytdlp.Ui.Client/dist/` и не запускают npm. Первая компилирует .NET, вторая использует предыдущую Release-сборку. Оба варианта включают `wwwroot/index.html` и собранные CSS/JS; исходники React и `node_modules` не публикуются. `--no-build` требует предварительного `dotnet build` с той же конфигурацией и актуальным `dist/`. При отсутствии готового UI публикация завершается ошибкой с подсказкой.

Совместная компиляция и публикация также доступны: `dotnet publish src/Ytdlp.Ui/Ytdlp.Ui.csproj -c Release -p:BuildFrontend=true -o artifacts/publish`. С `--no-build` React не компилируется даже при указанном флаге.

Запуск опубликованного приложения из его каталога:

```powershell
dotnet Ytdlp.Ui.dll --urls http://localhost:5080 --ConnectionStrings:AppDatabase "Data Source=./data/db/app.db"
```

## Конфигурация

Используются стандартные настройки ASP.NET Core: `Logging`, `AllowedHosts` и адрес прослушивания. Их можно переопределять переменными окружения, например `ASPNETCORE_URLS=http://+:8080` и `Logging__LogLevel__Default=Warning`.

Для демонстрационного входа: **`admin` / `demo-password`**. Эти значения заданы на бэкенде в `Admin:Username` и `Admin:Password` и переопределяются через `Admin__Username`, `Admin__Password`. JWT настраивается через `Jwt:Issuer`, `Jwt:Audience`, `Jwt:SigningKey` (переменные `Jwt__Issuer`, `Jwt__Audience`, `Jwt__SigningKey`). `SigningKey` задается в стандартном Base64 и должен декодироваться минимум в 32 байта (256 бит) для HS256. Пример ключа предназначен для демонстрационного режима; его Base64 сохраняет прежние байты ключа. Срок access token — 5 минут, абсолютный срок сессии — 12 часов от входа. Рестарт с прежней БД и ключом не требует нового входа. Выход отзывает только текущую сессию в SQLite.

Клиент хранит пару токенов и срок access token в `sessionStorage`. Перед административным запросом обновляет истекший access; при `401` обновляет пару и повторяет запрос один раз. Параллельные запросы используют один refresh; кратковременные сетевые ошибки и `500` сохраняют вход. После перезагрузки UI при необходимости обновляет access и проверяет сессию перед загрузкой закрытых данных. Окончательный `401` или выход очищает токены и административные данные. Пароль не сохраняется. Поздние ответы прежней сессии не восстанавливают вход после logout и не перезаписывают новый login.

При запуске требуется доступ к постоянному файлу SQLite и его каталогу. В `appsettings.json` задан `ConnectionStrings:AppDatabase` со значением `Data Source=/data/db/app.db`, которое читается через `IConfiguration.GetConnectionString`. Переопределение — `ConnectionStrings__AppDatabase`. Пустая строка подключения и режим SQLite Memory отклоняются. Относительный путь приводится к полному от текущего рабочего каталога процесса; тесты используют этот вариант. Для локального запуска выше передается отдельный путь в репозитории, а опубликованное приложение использует `./data/db/app.db` относительно каталога запуска. Промежуточные файлы и S3 пока не подключены. Основной сценарий MVP останется подключением к существующему MinIO.

В контейнере сохраняйте `/data` в volume: в нем находятся БД и сопутствующие SQLite-файлы `-wal`/`-shm`. Dockerfile создает `/data/db` с владельцем `$APP_UID`; Compose подключает именованный volume `app-data`. При bind mount каталог хоста должен быть доступен на запись этому UID. Обычные перезапуски и `docker compose down` сохраняют именованный volume; `docker compose down -v` удаляет его вместе с БД.

### Миграции SQLite

До production схема объединена в единственную миграцию `InitialPersistence`, включая nullable BLOB-хеш refresh и его уникальный индекс. Локальную БД, созданную прежними вариантами миграций, разработчик должен пересоздать вручную. Приложение не удаляет ее автоматически и не преобразует прежнюю схему.

Локальный инструмент `dotnet-ef` закреплен в `.config/dotnet-tools.json`; его версия согласована с пакетами EF Core в CPM. После Release-сборки можно создать отдельную БД для проверки схемы, не запуская API:

```powershell
dotnet tool restore
$databasePath = Join-Path (Get-Location) 'artifacts/sqlite-step2/app.db'
New-Item -ItemType Directory -Force -Path (Split-Path $databasePath) | Out-Null
dotnet ef database update --project src/Ytdlp.Ui --context AppDbContext --configuration Release --no-build -- --ConnectionStrings:AppDatabase "Data Source=$databasePath"
```

`dotnet-ef` получает `AppDbContext` из DI хоста приложения. Источники конфигурации и их приоритет совпадают с обычным запуском ASP.NET Core. Инструмент создает хост для получения сервисов, но не запускает HTTP-сервер. Относительный путь считается от рабочего каталога процесса; пример передает абсолютный путь от корня репозитория и заранее создает каталог БД. Файлы БД и `artifacts/` не коммитятся. Применение миграции через CLI проверено на отдельной БД. Программная инициализация `AppDbContext.InitializeAsync` с настоящими миграциями проверена тестами на временных БД.

## Docker и CI

Dockerfile содержит независимые этапы сборки React в Node.js и сборки .NET, затем ASP.NET runtime. Готовый `dist/` передается в этап .NET перед публикацией; Node.js/npm для сборки UI в этом этапе не нужны. Статический UI собирается на платформе сборщика и используется для обеих целевых архитектур. Финальный контейнер запускается непривилегированным пользователем, имеет доступ на запись в `/data` и слушает порт 8080. Медиазависимости и вспомогательный MinIO в Compose будут добавлены при реализации соответствующих компонентов.

Команды для окружения с Docker:

```powershell
docker build -t ytdlp-ui:dev .
docker run --rm -p 5080:8080 -v ytdlp-ui-data:/data ytdlp-ui:dev
docker compose up --build
docker buildx build --platform linux/amd64,linux/arm64 --output type=oci,dest=artifacts/ytdlp-ui.tar .
```

CI запускается на push и pull request в `master`. Общие шаги оформлены по [workflow ResultService](https://github.com/Texnokaktus-ProgOlymp/ResultService/blob/master/.github/workflows/dotnet.yml): Setup .NET, Restore dependencies, Build, Test, Version, QEMU, Buildx, вход в реестр, metadata и Build and push Docker image. Дополнительно настроены Node.js, тесты React и проверка публикации.

Job `build` отдельно восстанавливает зависимости и собирает React, затем собирает .NET, выполняет тесты без внешних сервисов и проверяет оба варианта публикации. Содержимое `wwwroot` сравнивается с готовым `dist`. Job `docker` после него собирает `linux/amd64,linux/arm64` через Buildx/QEMU. Приложение и готовые контейнеры в CI не запускаются; smoke-тестов образов нет. При push в `master` образ публикуется в `ghcr.io/<repository-owner>/ytdlp-ui`; для PR вход в GHCR и публикация отключены. Проверка запуска на целевых архитектурах выполняется отдельно.

Тег имеет вид `YYYY.M.D.<github.run_number>`: дата берется из даты коммита в `Europe/Moscow`. Имя образа приводится к нижнему регистру; теги и OCI labels формирует Docker metadata action. Автоматический деплой не настроен.

**Проверено локально:** раздельная сборка React и .NET Release без вызова npm из .NET, сборка API без готового UI, совместная сборка по флагу `BuildFrontend=true` и отдельный target `BuildFrontend`, отказ публикации без UI; NUnit и Vitest; обычная публикация и публикация с `--no-build`, совпадение файлов UI с `dist`. Ранее проверены вход, восстановление сессии, создание задания и фокус диалогов в браузере; мобильная ширина 390 px без горизонтального скролла. Workflow проверен через actionlint, Compose — через `docker compose config --quiet`.

**Не проверено локально:** сборка и запуск Docker-образов на AMD64/ARM64 и выполнение workflow на GitHub Actions. Docker daemon недоступен. Запуск приложения в CI отключён по указанию пользователя.

Для подключения инициализации SQLite проверены Release-сборка, NUnit-тесты первого и повторного запуска через TestServer и сохранение данных, отказ запуска при ошибке БД, публикация с фронтендом, применение миграции через `dotnet-ef` и конфигурация Compose. Новые команды запуска контейнера приведены для окружения с Docker; локально они не выполнялись.

Для SQLite-авторизации и refresh проверены Release-сборка, NUnit/API-тесты на свежих файловых БД с единственной начальной миграцией, TypeScript (`tsc --noEmit`), `vite build`, Vitest и `dotnet publish --no-build` с совпадением опубликованного UI с `dist`. В текущей среде npm отсутствует в PATH, поэтому локальные frontend CLI запущены с bundled Node; для Vite/Vitest разрешен запуск вне sandbox из-за Windows spawn EPERM. Новые browser- и Docker-проверки в этом этапе не выполнялись.
