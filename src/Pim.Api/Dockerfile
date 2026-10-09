# 注：前端（pim-web 仓）的构建产物不再内嵌本镜像。
# 需要 PIM API 同源伺服 SPA 时，把 pim-web 的构建产物解包到 src/Pim.Api/wwwroot 之后再构建本镜像，
# 或由部署层的反向代理伺服前端静态文件。
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
WORKDIR /app
COPY --from=server-build /app/publish .
EXPOSE 5000

RUN apt-get update \
    && apt-get install -y --no-install-recommends supervisor tini \
    && rm -rf /var/lib/apt/lists/*

COPY scripts/docker/supervisord.conf /etc/supervisor/supervisord.conf
COPY scripts/docker/entrypoint.sh /entrypoint.sh

RUN chmod 755 /entrypoint.sh \
    && chmod 644 /etc/supervisor/supervisord.conf

ENTRYPOINT ["tini", "--", "/entrypoint.sh"]
