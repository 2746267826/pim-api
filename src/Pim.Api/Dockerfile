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
RUN dotnet restore "Pim.Api/Pim.Api.csproj"
COPY . .
COPY --from=client-build /dist/ src/Pim.Api/wwwroot/
WORKDIR "/src/src/Pim.Api"
RUN dotnet publish "Pim.Api.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=server-build /app/publish .
EXPOSE 5000
ENTRYPOINT ["dotnet", "Pim.Api.dll"]
