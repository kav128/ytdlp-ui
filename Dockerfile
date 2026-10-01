FROM node:24.19.0-bookworm-slim AS node

FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules/npm /usr/local/lib/node_modules/npm
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/Directory.Build.props src/Directory.Build.props
COPY src/Ytdlp.Ui/Ytdlp.Ui.csproj src/Ytdlp.Ui/Ytdlp.Ui.csproj
RUN dotnet restore src/Ytdlp.Ui/Ytdlp.Ui.csproj
COPY src/ src/
RUN dotnet publish src/Ytdlp.Ui/Ytdlp.Ui.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ytdlp.Ui.dll"]
