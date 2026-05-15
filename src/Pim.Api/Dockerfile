FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/Pim.Core/Pim.Core.csproj", "Pim.Core/"]
COPY ["src/Pim.Infrastructure/Pim.Infrastructure.csproj", "Pim.Infrastructure/"]
COPY ["src/Pim.Api/Pim.Api.csproj", "Pim.Api/"]
COPY ["src/modules/Pim.Module.Calendar/Pim.Module.Calendar.csproj", "modules/Pim.Module.Calendar/"]
RUN dotnet restore "Pim.Api/Pim.Api.csproj"
COPY . .
WORKDIR "/src/src/Pim.Api"
RUN dotnet publish "Pim.Api.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*
EXPOSE 5000
ENTRYPOINT ["dotnet", "Pim.Api.dll"]
