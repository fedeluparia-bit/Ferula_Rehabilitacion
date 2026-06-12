# ── Etapa 1: Build ────────────────────────────────────────────────────────────
# Se usa el SDK completo para restaurar, compilar y publicar.
# Copiar el .csproj primero y restaurar antes que el código fuente permite
# que Docker reutilice esta capa en rebuilds cuando solo cambia código (no deps).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Ferula.Api.csproj", "."]
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Etapa 2: Runtime ──────────────────────────────────────────────────────────
# Imagen mcr.microsoft.com/dotnet/aspnet es ~3× más pequeña que el SDK
# porque solo contiene el runtime de ASP.NET, sin compilador ni herramientas.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# EXPOSE es documentación; el puerto real lo inyecta Render vía la variable
# de entorno PORT en tiempo de ejecución. El código lee PORT ?? "8080".
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Ferula.Api.dll"]
