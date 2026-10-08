# syntax=docker/dockerfile:1
#
# Fake da API Solides DP (Tangerino) para desenvolvimento/homologacao da integracao.
# O contexto de build e a RAIZ do repositorio (precisa de global.json, Directory.*.props e spec/):
#
#   docker build -f src/SolidesDP.Fake/Dockerfile -t solides-dp-fake .
#   docker run --rm -p 5080:8080 solides-dp-fake
#
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

# Configuracao de build compartilhada + Swagger do fornecedor (embutido no assembly do fake).
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY spec/ spec/

# Restore em modo travado (packages.lock.json) antes de copiar o codigo, para aproveitar o cache de camadas.
COPY src/SolidesDP.Fake/SolidesDP.Fake.csproj src/SolidesDP.Fake/packages.lock.json src/SolidesDP.Fake/
RUN dotnet restore src/SolidesDP.Fake/SolidesDP.Fake.csproj --locked-mode

COPY src/SolidesDP.Fake/ src/SolidesDP.Fake/
RUN dotnet publish src/SolidesDP.Fake/SolidesDP.Fake.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Dentro do container o fake escuta na 8080 (sem root).
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "SolidesDP.Fake.dll"]
