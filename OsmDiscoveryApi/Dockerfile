FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src
COPY ["OsmDiscoveryApi/OsmDiscoveryApi.csproj", "OsmDiscoveryApi/"]
RUN dotnet restore "OsmDiscoveryApi/OsmDiscoveryApi.csproj"
COPY . .
WORKDIR "/src/OsmDiscoveryApi"
RUN dotnet build "OsmDiscoveryApi.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "OsmDiscoveryApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS final
WORKDIR /app
EXPOSE 8080
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "OsmDiscoveryApi.dll"]