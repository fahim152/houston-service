# syntax=docker/dockerfile:1

# ---- build: restore and publish with the .NET 10 SDK ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (only the project file), so this layer is cached until dependencies change.
COPY global.json ./
COPY src/Rockets.Api/Rockets.Api.csproj src/Rockets.Api/
RUN dotnet restore src/Rockets.Api/Rockets.Api.csproj

COPY src/ src/
RUN dotnet publish src/Rockets.Api/Rockets.Api.csproj -c Release -o /app --no-restore

# ---- runtime: the smaller ASP.NET Core image, running as a non-root user ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# appsettings.json binds to localhost (right for `dotnet run`); inside a container the service must listen on all
# interfaces, otherwise it can't be reached through the published port. The base image's default port (8080) is
# cleared so it doesn't conflict with this setting.
ENV Urls=http://+:8088 \
    ASPNETCORE_HTTP_PORTS=
EXPOSE 8088

USER $APP_UID
ENTRYPOINT ["dotnet", "Rockets.Api.dll"]
