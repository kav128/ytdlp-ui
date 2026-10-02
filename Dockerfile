FROM --platform=$BUILDPLATFORM node:24.19.0-bookworm-slim AS frontend
WORKDIR /source
COPY src/Ytdlp.Ui.Client/package*.json ./
RUN npm ci --no-audit --no-fund
COPY src/Ytdlp.Ui.Client/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/Directory.Build.props src/Directory.Build.props
COPY src/Ytdlp.Ui/Ytdlp.Ui.csproj src/Ytdlp.Ui/Ytdlp.Ui.csproj
RUN dotnet restore src/Ytdlp.Ui/Ytdlp.Ui.csproj
COPY src/Ytdlp.Ui/ src/Ytdlp.Ui/
COPY --from=frontend /source/dist/ src/Ytdlp.Ui.Client/dist/
RUN dotnet publish src/Ytdlp.Ui/Ytdlp.Ui.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ytdlp.Ui.dll"]
