# 前端与 API 同仓：镜像构建时直接从 web/client-web 构建静态产物，不再拉取
# ghcr.io/2746267826/pim-web-dist，也不再依赖跨仓 dispatch。
#
# 版本信息仍写进 /app/wwwroot/web-version.json（version / gitSha / builtAt），
# 三个值由 CI 的同一次构建注入，避免「镜像里的前端版本」与实际代码不一致。
FROM node:22-bookworm-slim AS web-build
ARG PIM_VERSION=0.0.0-local
ARG PIM_WEB_VERSION=0.0.0-local
ARG PIM_WEB_SHA=0000000
ARG PIM_WEB_BUILT_AT=1970-01-01T00:00:00Z
WORKDIR /web
COPY ["web/client-web/package.json", "web/client-web/package-lock.json", "web/client-web/.npmrc", "./"]
RUN npm ci --no-audit --no-fund
COPY web/client-web/ ./
COPY web/scripts/ci/write-web-version.sh /usr/local/bin/write-web-version.sh
ENV VITE_APP_VERSION="$PIM_WEB_VERSION" \
    VITE_GIT_SHA="$PIM_WEB_SHA"
RUN set -eux; \
    npm run build; \
    test -s dist/index.html; \
    test -d dist/assets; \
    test -n "$(find dist/assets -type f -print -quit)"; \
    bash /usr/local/bin/write-web-version.sh --out dist/web-version.json \
      --version "$PIM_WEB_VERSION" --git-sha "$PIM_WEB_SHA" --built-at "$PIM_WEB_BUILT_AT"; \
    test -s dist/web-version.json

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS server-build
ARG PIM_VERSION=0.0.0-local
ARG ASSEMBLY_VERSION=0.0.0.0
WORKDIR /src
COPY ["src/Pim.Core/Pim.Core.csproj", "Pim.Core/"]
COPY ["src/Pim.Infrastructure/Pim.Infrastructure.csproj", "Pim.Infrastructure/"]
COPY ["src/Pim.Api/Pim.Api.csproj", "Pim.Api/"]
COPY ["src/modules/Pim.Module.Calendar/Pim.Module.Calendar.csproj", "modules/Pim.Module.Calendar/"]
COPY ["src/modules/Pim.Module.PcTracker/Pim.Module.PcTracker.csproj", "modules/Pim.Module.PcTracker/"]
COPY ["src/modules/Pim.Module.Files/Pim.Module.Files.csproj", "modules/Pim.Module.Files/"]
COPY ["src/modules/Pim.Module.QuickNotes/Pim.Module.QuickNotes.csproj", "modules/Pim.Module.QuickNotes/"]
COPY ["src/modules/Pim.Module.Mobile/Pim.Module.Mobile.csproj", "modules/Pim.Module.Mobile/"]
RUN dotnet restore "Pim.Api/Pim.Api.csproj"
COPY . .
WORKDIR "/src/src/Pim.Api"
RUN dotnet publish "Pim.Api.csproj" -c Release -o /app/publish \
  -p:InformationalVersion="$PIM_VERSION" -p:Version="$ASSEMBLY_VERSION" -p:FileVersion="$ASSEMBLY_VERSION"

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# 三个 label 是契约：docker inspect 直接读出「这个镜像里前端是哪版」。
ARG PIM_VERSION=0.0.0-local
ARG PIM_WEB_VERSION=unknown
ARG PIM_WEB_SHA=unknown
LABEL org.pim.api-version="$PIM_VERSION" \
      org.pim.web-version="$PIM_WEB_VERSION" \
      org.pim.web-sha="$PIM_WEB_SHA"
WORKDIR /app
COPY --from=server-build /app/publish .

# 前端静态产物：来自同仓 Node 构建阶段。
COPY --from=web-build /web/dist/ /app/wwwroot/

# COPY 后校验：缺 index.html / assets/ / web-version.json 一律硬失败。
# 这就是「不许产出没前端或半前端镜像」这道门禁的落点。
RUN set -eux; \
    test -s /app/wwwroot/index.html; \
    test -s /app/wwwroot/web-version.json; \
    test -d /app/wwwroot/assets; \
    test -n "$(find /app/wwwroot/assets -type f -print -quit)"; \
    echo "embedded web dist: $(cat /app/wwwroot/web-version.json)"

EXPOSE 5000

RUN apt-get update \
    && apt-get install -y --no-install-recommends supervisor tini \
    && rm -rf /var/lib/apt/lists/*

COPY scripts/docker/supervisord.conf /etc/supervisor/supervisord.conf
COPY scripts/docker/entrypoint.sh /entrypoint.sh

RUN chmod 755 /entrypoint.sh \
    && chmod 644 /etc/supervisor/supervisord.conf

ENTRYPOINT ["tini", "--", "/entrypoint.sh"]
