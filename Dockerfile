# Build from this repo (or compose context ../kithara):
#
#   docker build -t kithara .
#   docker build --target test .
#
# Restores Bardie.Logos.* / Bardie.Module.* from nuget.org.
#
# META-OPS-002: Alpine final + bare libav packages (no ffmpeg CLI metapackage).
# Build on Debian SDK so Grpc.Tools protoc (glibc) runs; publish for linux-musl-x64.
#
# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY Kithara.sln ./
COPY libs libs/
COPY src/Kithara src/Kithara/

RUN dotnet restore src/Kithara/Kithara.csproj -r linux-musl-x64 \
 && dotnet publish src/Kithara/Kithara.csproj \
      -c Release -r linux-musl-x64 --self-contained false \
      -o /app/publish --no-restore

# Encoder integration tests — FFmpeg.AutoGen 6.1.x sonames (libavcodec.so.60).
# Debian SDK (Grpc.Tools) + apt ffmpeg matching AutoGen 6.1.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS test
WORKDIR /src
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
ENV BARDIE_FFMPEG_ROOT=/usr/lib/x86_64-linux-gnu
COPY Directory.Build.props Directory.Packages.props ./
COPY Kithara.sln ./
COPY libs libs/
COPY src src/
COPY tests tests/
RUN dotnet restore Kithara.sln
RUN dotnet test Kithara.sln -c Release --no-restore

# Pin alpine3.22: floating `10.0-alpine` is 3.23+ (ffmpeg 8 → libavutil.so.60) which
# does not match FFmpeg.AutoGen 6.1.0.1 (expects libavutil.so.58 / libavcodec.so.60).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine3.22 AS final
WORKDIR /app

# Neck PCM→MP3 via FFmpeg.AutoGen: shared libs only (Alpine 3.22 ffmpeg 6.1.x).
# Do not install the `ffmpeg` CLI metapackage.
RUN apk add --no-cache \
      ffmpeg-libavcodec \
      ffmpeg-libavformat \
      ffmpeg-libavutil \
      ffmpeg-libswresample \
    && mkdir -p /data/mtls /data/db /data/blobs /audio/strunas \
    && chown -R "$APP_UID":"$APP_UID" /data /app /audio

COPY --from=build /app/publish .
RUN chown -R "$APP_UID":"$APP_UID" /app

USER $APP_UID
ENV ASPNETCORE_URLS= \
    BARDIE_GRPC_TLS_DATA_PATH=/data/mtls \
    BARDIE_STRUNA_FIFO_PATH=/audio \
    BARDIE_STORAGE_PATH=/data/blobs \
    BARDIE_FFMPEG_ROOT=/usr/lib \
    DbProvider=sqlite \
    DbConnectionString="Data Source=/data/db/kithara.db"

EXPOSE 8080 5000
# Busybox wget is on Alpine base — avoid curl bloat.
HEALTHCHECK --interval=30s --timeout=3s --start-period=25s --retries=3 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Kithara.dll"]
