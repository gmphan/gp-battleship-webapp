# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (leverage layer cache)
COPY GpBattleshipWebapp.csproj ./
RUN dotnet restore

# Copy everything else and publish
COPY . ./
RUN dotnet publish GpBattleshipWebapp.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=build /app/publish ./

# The .NET 8+ ASP.NET images ship a non-root "app" user (UID 1654).
# Run as it for safer container execution.
USER app

# Listen on 8080 inside the container
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "GpBattleshipWebapp.dll"]

