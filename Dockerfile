FROM --platform=$BUILDPLATFORM node:24.19.0-bookworm-slim AS frontend
WORKDIR /source
COPY src/Ytdlp.Ui.Client/package*.json ./
RUN npm ci --no-audit --no-fund
COPY src/Ytdlp.Ui.Client/ ./
RUN npm run build

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
ARG TARGETARCH
WORKDIR /source
COPY global.json Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/Directory.Build.props src/Directory.Build.props
COPY src/Ytdlp.Ui/Ytdlp.Ui.csproj src/Ytdlp.Ui/Ytdlp.Ui.csproj
RUN dotnet restore src/Ytdlp.Ui/Ytdlp.Ui.csproj -a "$TARGETARCH"
COPY src/Ytdlp.Ui/ src/Ytdlp.Ui/
COPY --from=frontend /source/dist/ src/Ytdlp.Ui.Client/dist/
RUN dotnet publish src/Ytdlp.Ui/Ytdlp.Ui.csproj -c Release -a "$TARGETARCH" --self-contained false --no-restore -o /app/publish /p:UseAppHost=false

# This stage resolves for TARGETPLATFORM, so the copied Node binary matches the runtime architecture.
FROM node:24.19.0-bookworm-slim AS node-runtime

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS media-runtime
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg=7:6.1.1-3ubuntu5 python3-venv libgssapi-krb5-2 ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=node-runtime /usr/local/bin/node /usr/local/bin/node
COPY build/media-requirements.txt /opt/media-requirements.txt
RUN python3 -m venv /opt/media-env \
    && /opt/media-env/bin/pip install --no-cache-dir --only-binary=:all: --require-hashes -r /opt/media-requirements.txt
ENV PATH="/opt/media-env/bin:${PATH}" PYTHONDONTWRITEBYTECODE=1

FROM media-runtime AS final
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /data/keys /data/work && chown -R "$APP_UID" /data
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=6s --start-period=30s --retries=3 CMD ["python3", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:8080/health/ready', timeout=5).close()"]
ENTRYPOINT ["dotnet", "Ytdlp.Ui.dll"]
