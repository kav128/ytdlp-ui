# yt-dlp WebUI

Приложение из [MVP-SPEC.md](MVP-SPEC.md): ASP.NET Core 10 Minimal API и React с отдельными каталогами и сборками. Kestrel раздает API и собранный интерфейс с одного адреса.

Сейчас реализован UI с моковым бэкендом: публичная библиотека, вход администратора, очередь и история загрузок, создание по URL, повтор, отмена и удаление файла. Интерфейс следует Material 3, адаптируется к телефону и десктопу, выбирает системную тему и язык EN/RU по браузеру. Форма входа открывается справа сверху на десктопе; административные функции появляются на той же странице после проверки входа.

Моковый API хранит данные и сессии в памяти. В библиотеке находятся три небольших демонстрационных текстовых файла, которые действительно можно скачать. Очередь содержит примеры разных состояний, включая ошибку отправки и ошибку очистки. Создание и повтор ставят задания в очередь, но мок не выполняет реальные загрузки и не увеличивает прогресс по таймеру. После перезапуска все изменения сбрасываются. SQLite, MinIO, yt-dlp и FFmpeg будут подключены на следующих этапах; сейчас внешние сервисы не требуются.

Каталоги компонентов созданы по [TECHNICAL-DESIGN.md](TECHNICAL-DESIGN.md); пустые сохраняются в Git через `.gitkeep`.

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
    Processing/
    Infrastructure/{Data,Media,Storage,Authentication}/
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
dotnet run --project src/Ytdlp.Ui -c Release --no-build
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
| `POST /api/auth/login` | Не требуется | `{ username, password }` | `200`, `{ accessToken, tokenType: "Bearer", expiresAt }`; `401` при неверных данных |
| `POST /api/auth/logout` | Администратор | Без тела | `204`, отзыв текущей сессии |
| `GET /api/downloads` | Администратор | Без тела | `200`, массив заданий с состояниями, действиями и моковой историей |
| `GET /api/downloads/{id}` | Администратор | Без тела | `200`, задание; `404`, если отсутствует |
| `POST /api/downloads` | Администратор | `{ url }` | `201` с `Location` для нового задания; `202` для повтора удалённого результата; `400 invalid_url`, `409 download_already_exists` |
| `POST /api/downloads/{id}/retry` | Администратор | Без тела | `202`, новая попытка с прежним ID задания; `404` либо `409 invalid_state` |
| `POST /api/downloads/{id}/cancel` | Администратор | Без тела | `202`, состояние Canceled; `404` либо `409 invalid_state` |

Административные запросы требуют `Authorization: Bearer <token>`; без входа возвращается `401`. JWT проверяется по подписи, HS256, issuer, audience, сроку и активности моковой сессии. Cookie и query-параметры не используются для авторизации. Ошибки команд возвращаются как Problem Details с `code` для локализации в UI.

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

NUnit-тесты используют встроенный `WebApplicationFactory`/TestServer без отдельного процесса сервера, контейнеров и хранилищ. Проверяются контракт API, выдача файлов, ограничения доступа, отзыв JWT, запрет токенов в cookie/query, дубликаты URL, история после удаления и запрет отмены очистки до и после повтора. Проверки страницы React и доступности собранного JavaScript также сохранены. NSubstitute включен в общие зависимости тестов для будущих заглушек внешних компонентов.

Vitest и Testing Library проверяют публичную библиотеку без запросов закрытых данных, проверку сохранённого токена, вход/выход, валидацию URL, обработку `401` и поздних ответов после выхода. Отдельно проверяются выбор языка браузера и ресурсы EN/RU.

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
dotnet Ytdlp.Ui.dll --urls http://localhost:5080
```

## Конфигурация

Используются стандартные настройки ASP.NET Core: `Logging`, `AllowedHosts` и адрес прослушивания. Их можно переопределять переменными окружения, например `ASPNETCORE_URLS=http://+:8080` и `Logging__LogLevel__Default=Warning`.

Для демонстрационного входа: **`admin` / `demo-password`**. Эти значения заданы на бэкенде в `Admin:Username` и `Admin:Password` и переопределяются через `Admin__Username`, `Admin__Password`. JWT настраивается через `Jwt:Issuer`, `Jwt:Audience`, `Jwt:SigningKey` (переменные `Jwt__Issuer`, `Jwt__Audience`, `Jwt__SigningKey`). Пример ключа предназначен для демонстрационного режима. Срок токена — 12 часов; сессия отзывается при выходе и теряется при перезапуске мокового сервера.

Клиент хранит JWT в `sessionStorage`, проверяет его после перезагрузки и очищает вместе с административными данными при выходе/`401` текущей сессии. Пароль не сохраняется. Поздние ответы предыдущей сессии игнорируются.

Моковый этап не требует volumes. Настройки S3 и постоянных путей пока не подключены. В последующих итерациях SQLite и промежуточные файлы будут размещены в сохраняемом `/data`; основной сценарий MVP останется подключением к существующему MinIO.

## Docker и CI

Dockerfile содержит независимые этапы сборки React в Node.js и сборки .NET, затем ASP.NET runtime. Готовый `dist/` передается в этап .NET перед публикацией; Node.js/npm для сборки UI в этом этапе не нужны. Статический UI собирается на платформе сборщика и используется для обеих целевых архитектур. Финальный контейнер запускается непривилегированным пользователем и слушает порт 8080. Медиазависимости и вспомогательный MinIO в Compose будут добавлены при реализации соответствующих компонентов.

Команды для окружения с Docker:

```powershell
docker build -t ytdlp-ui:dev .
docker run --rm -p 5080:8080 ytdlp-ui:dev
docker compose up --build
docker buildx build --platform linux/amd64,linux/arm64 --output type=oci,dest=artifacts/ytdlp-ui.tar .
```

CI запускается на push и pull request в `master`. Общие шаги оформлены по [workflow ResultService](https://github.com/Texnokaktus-ProgOlymp/ResultService/blob/master/.github/workflows/dotnet.yml): Setup .NET, Restore dependencies, Build, Test, Version, QEMU, Buildx, вход в реестр, metadata и Build and push Docker image. Дополнительно настроены Node.js, тесты React и проверка публикации.

Job `build` отдельно восстанавливает зависимости и собирает React, затем собирает .NET, выполняет тесты без внешних сервисов и проверяет оба варианта публикации. Содержимое `wwwroot` сравнивается с готовым `dist`. Job `docker` после него собирает `linux/amd64,linux/arm64` через Buildx/QEMU. Приложение и готовые контейнеры в CI не запускаются; smoke-тестов образов нет. При push в `master` образ публикуется в `ghcr.io/<repository-owner>/ytdlp-ui`; для PR вход в GHCR и публикация отключены. Проверка запуска на целевых архитектурах выполняется отдельно.

Тег имеет вид `YYYY.M.D.<github.run_number>`: дата берется из даты коммита в `Europe/Moscow`. Имя образа приводится к нижнему регистру; теги и OCI labels формирует Docker metadata action. Автоматический деплой не настроен.

**Проверено локально:** раздельная сборка React и .NET Release без вызова npm из .NET, сборка API без готового UI, совместная сборка по флагу `BuildFrontend=true` и отдельный target `BuildFrontend`, отказ публикации без UI; NUnit и Vitest; обычная публикация и публикация с `--no-build`, совпадение файлов UI с `dist`. Ранее проверены вход, восстановление сессии, создание задания и фокус диалогов в браузере; мобильная ширина 390 px без горизонтального скролла. Workflow проверен через actionlint, Compose — через `docker compose config --quiet`.

**Не проверено локально:** сборка и запуск Docker-образов на AMD64/ARM64 и выполнение workflow на GitHub Actions. Docker daemon недоступен. Запуск приложения в CI отключён по указанию пользователя.
