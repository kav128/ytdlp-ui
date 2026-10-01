# yt-dlp WebUI

Каркас приложения из [MVP-SPEC.md](MVP-SPEC.md): ASP.NET Core 10 Minimal API и React в одном веб-проекте. Kestrel раздает API и собранный интерфейс с одного адреса.

Сейчас бэкенд возвращает `Hello World`, а React показывает `Hello World`. Каталоги будущих компонентов созданы по [TECHNICAL-DESIGN.md](TECHNICAL-DESIGN.md) и сохраняются в Git через `.gitkeep`. Загрузки, авторизация, SQLite, MinIO и медиапроцессы будут реализованы в следующих итерациях. Для запуска каркаса они не требуются.

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
    Configuration/
    ClientApp/
      src/{api,components,features,locales,styles,test}/
tests/
  Directory.Build.props
  Ytdlp.Ui.Tests/
```

## Сборка и запуск

Требуются .NET SDK **10.0.401**, Node.js **24.19.0** и npm в `PATH`. SDK строго зафиксирован в `global.json`; CI и Docker используют ту же версию. NuGet-зависимости управляются через CPM; `NuGet.Config` ограничивает источники публичным `nuget.org`, чтобы сборка не зависела от пользовательских feeds. Зависимости React фиксируются в `ClientApp/package-lock.json`.

Из корня репозитория:

```powershell
dotnet restore Ytdlp.Ui.slnx
dotnet build Ytdlp.Ui.slnx -c Release --no-restore
dotnet run --project src/Ytdlp.Ui -c Release --no-build
```

Открыть `http://localhost:5080/`. Профиль запуска использует локальный HTTP без настройки сертификатов.

При `dotnet build` MSBuild автоматически выполняет `npm ci`, если зависимости отсутствуют или изменился манифест/lock-файл, затем проверку TypeScript и сборку Vite. Результат находится в `src/Ytdlp.Ui/wwwroot/` и не коммитится. Отдельно собирать фронтенд не требуется.

Для разработки React с обновлением страницы можно дополнительно запустить `npm run dev` в `src/Ytdlp.Ui/ClientApp/`. Vite проксирует `/api` на локальный бэкенд `http://localhost:5080`.

## API

| Метод и маршрут | Авторизация | Запрос | Ответ |
| --- | --- | --- | --- |
| `GET /api/hello` | Не требуется | Без тела и параметров | `200`, `Content-Type: text/plain; charset=utf-8`, тело `Hello World` |

Неизвестные пути `/api/*` возвращают `404` без HTML интерфейса. `GET /` возвращает страницу React; пути UI без расширения поддерживают SPA fallback. Отсутствующий статический ресурс возвращает `404`. Остальные методы бизнес-API из ТЗ пока не реализованы.

## Тесты без внешнего окружения

```powershell
dotnet test tests/Ytdlp.Ui.Tests/Ytdlp.Ui.Tests.csproj -c Release --no-build
```

После сборки решения запустить UI-тесты из `src/Ytdlp.Ui/ClientApp/`:

```powershell
npm test
```

NUnit-тесты используют встроенный `WebApplicationFactory`/TestServer без отдельного процесса сервера, контейнеров и хранилищ. Проверяются контракт API, неизвестные маршруты, страница React и доступность собранного JavaScript. NSubstitute включен в общие зависимости тестов для будущих заглушек внешних компонентов.

Vitest и Testing Library проверяют отображение `Hello World`, выбор первого поддерживаемого языка браузера и полноту ресурсов EN/RU. Приветствие пока одинаково в обоих языках согласно задаче. Страница адаптируется к ширине экрана и системной светлой/темной теме.

Контейнерных интеграционных тестов на этой стадии нет. При реализации MinIO они будут добавлены в отдельный проект с Testcontainers и обязательным запуском в CI; локальные тесты без Docker останутся отдельным набором.

## Публикация

```powershell
dotnet publish src/Ytdlp.Ui/Ytdlp.Ui.csproj -c Release --no-restore -o artifacts/publish
dotnet publish src/Ytdlp.Ui/Ytdlp.Ui.csproj -c Release --no-build -o artifacts/publish-no-build
```

Первая команда собирает приложение вместе с React. Вторая использует результат предыдущей Release-сборки и не запускает npm. Оба варианта включают `wwwroot/index.html` и собранные CSS/JS; исходники `ClientApp` и `node_modules` не публикуются. `--no-build` требует предварительного `dotnet build` с той же конфигурацией.

Запуск опубликованного приложения из его каталога:

```powershell
dotnet Ytdlp.Ui.dll --urls http://localhost:5080
```

## Конфигурация

Сейчас используются только стандартные настройки ASP.NET Core: `Logging`, `AllowedHosts` и адрес прослушивания. Их можно переопределять переменными окружения, например `ASPNETCORE_URLS=http://+:8080` и `Logging__LogLevel__Default=Warning`.

Настройки администратора, JWT, S3 и постоянных путей из технического проекта пока не подключены. Каркас не хранит данные и не требует volumes. В последующих итерациях SQLite и промежуточные файлы будут размещены в сохраняемом `/data`; основной сценарий MVP останется подключением к существующему MinIO.

## Docker и CI

Dockerfile содержит этапы Node.js, сборки .NET и ASP.NET runtime. Сборка React выполняется через тот же `dotnet publish`. Финальный контейнер запускается непривилегированным пользователем и слушает порт 8080. Медиазависимости и вспомогательный MinIO в Compose будут добавлены при реализации соответствующих компонентов.

Команды для окружения с Docker:

```powershell
docker build -t ytdlp-ui:dev .
docker run --rm -p 5080:8080 ytdlp-ui:dev
docker compose up --build
docker buildx build --platform linux/amd64,linux/arm64 --output type=oci,dest=artifacts/ytdlp-ui.tar .
```

CI запускается на push и pull request в `master`. Общие шаги оформлены по [workflow ResultService](https://github.com/Texnokaktus-ProgOlymp/ResultService/blob/master/.github/workflows/dotnet.yml): Setup .NET, Restore dependencies, Build, Test, Version, QEMU, Buildx, вход в реестр, metadata и Build and push Docker image. Дополнительно настроены Node.js, тесты React и проверка публикации.

Job `build` собирает решение, выполняет тесты без внешних сервисов и проверяет оба варианта публикации. Job `docker` после него собирает `linux/amd64,linux/arm64` через Buildx/QEMU. Приложение и готовые контейнеры в CI не запускаются; smoke-тестов образов нет. При push в `master` образ публикуется в `ghcr.io/<repository-owner>/ytdlp-ui`; для PR вход в GHCR и публикация отключены. Проверка запуска на целевых архитектурах выполняется отдельно.

Тег имеет вид `YYYY.M.D.<github.run_number>`: дата берется из даты коммита в `Europe/Moscow`. Имя образа приводится к нижнему регистру; теги и OCI labels формирует Docker metadata action. Автоматический деплой не настроен.

**Проверено локально:** сборка Release, NUnit и Vitest, обычная публикация и публикация с `--no-build`, наличие UI в опубликованном приложении. **Не проверено локально:** сборка и запуск Docker-образов на AMD64/ARM64, Compose и сам GitHub Actions workflow. Docker daemon недоступен; эти команды приведены для последующей проверки в подходящем окружении.
