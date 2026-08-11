FROM node:22-alpine AS client-build
WORKDIR /client
COPY src/client-web/package.json src/client-web/package-lock.json ./
RUN npm ci
COPY src/client-web .
RUN npx vite build --outDir /dist

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS server-build
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
COPY --from=client-build /dist/ src/Pim.Api/wwwroot/
WORKDIR "/src/src/Pim.Api"
RUN dotnet publish "Pim.Api.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=server-build /app/publish .
EXPOSE 5000 22

RUN apt-get update \
    && apt-get install -y --no-install-recommends openssh-server supervisor tini \
    && rm -rf /var/lib/apt/lists/* \
    && useradd -r -u 1001 -m -d /home/pimlog -s /bin/bash pimlog

COPY scripts/docker/sshd-pim.conf /etc/ssh/sshd_config.d/pim.conf
COPY scripts/docker/supervisord.conf /etc/supervisor/supervisord.conf
COPY scripts/docker/entrypoint.sh /entrypoint.sh
COPY scripts/docker/pim-log-cat.sh /usr/local/bin/pim-log-cat.sh

RUN chmod 755 /entrypoint.sh /usr/local/bin/pim-log-cat.sh \
    && chmod 644 /etc/ssh/sshd_config.d/pim.conf /etc/supervisor/supervisord.conf

ENTRYPOINT ["tini", "--", "/entrypoint.sh"]
