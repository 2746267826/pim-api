# 前端（pim-web 仓）的静态产物随本镜像一起发布：构建期从 pim-web 推的**产物镜像**
# ghcr.io/2746267826/pim-web-dist 取用（tag 契约：sha-<7 位短 SHA>，滚动 tag latest）。
#
# 该包必须是 public：本 workflow 的 registry 登录发生在镜像构建**之后**、且只在发版时执行，
# 所以构建阶段默认没有登录态 —— 只能匿名拉取（REQ-1/REQ-3 的契约，不接受在 pim-api 侧
# 新增 registry 凭证）。取不到前端来源时构建必须硬失败，不许产出「没前端 / 半前端」的镜像。
#
# 构建来源由 PIM_WEB_DIST_SOURCE 指定，CI 传的是"不可变 tag + digest"：
#   ghcr.io/2746267826/pim-web-dist:sha-<7>@sha256:...
# 用 digest 钉住来源是刻意的：tag 在核对之后、BuildKit 解析之前被改动，也不会烘进另一份字节
# （builder 是 docker-container driver，看不到宿主机的本地 tag，所以"本地打 tag"约束不住它）。
# 不传该 build-arg 时退回契约里的滚动 tag latest，供本地临时构建使用。
# 契约文件：产物镜像根目录 /web-version.json（版本号 / git sha / 构建时间 UTC）。
#
# 已废弃的旧路径：手工把 pim-web 的 dist 解包到 src/Pim.Api/wwwroot 再 build ——
# 不再保留，避免两种姿势并存导致漂移。
ARG PIM_WEB_DIST_SOURCE=ghcr.io/2746267826/pim-web-dist:latest

FROM ${PIM_WEB_DIST_SOURCE} AS web-dist

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
# 三个 label 是契约（REQ-9）：docker inspect 直接读出「这个镜像里前端是哪版」。
ARG PIM_VERSION=0.0.0-local
ARG PIM_WEB_VERSION=unknown
ARG PIM_WEB_SHA=unknown
LABEL org.pim.api-version="$PIM_VERSION" \
      org.pim.web-version="$PIM_WEB_VERSION" \
      org.pim.web-sha="$PIM_WEB_SHA"
WORKDIR /app
COPY --from=server-build /app/publish .

# 前端静态产物：整份来自产物镜像的根目录（该镜像只含静态文件）。
COPY --from=web-dist / /app/wwwroot/

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
