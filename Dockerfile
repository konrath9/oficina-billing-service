# ---- Build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copia os .csproj e restaura dependências (camada cacheada)
COPY nuget.config .
COPY OficinaBilling.Domain/OficinaBilling.Domain.csproj OficinaBilling.Domain/
COPY OficinaBilling.Api/OficinaBilling.Api.csproj OficinaBilling.Api/
RUN dotnet restore OficinaBilling.Api/OficinaBilling.Api.csproj

COPY OficinaBilling.Domain/ OficinaBilling.Domain/
COPY OficinaBilling.Api/ OficinaBilling.Api/
RUN dotnet publish OficinaBilling.Api/OficinaBilling.Api.csproj -c Release -o /app/publish --no-restore

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# Executa como usuário não-root
RUN adduser --disabled-password --gecos "" appuser
USER appuser

WORKDIR /app

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "OficinaBilling.Api.dll"]
