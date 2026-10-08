# syntax=docker/dockerfile:1
#
# Worker IntegracaoSolidesDP em container (Linux). Contexto de build = raiz do repositório:
#
#   docker build -f src/IntegracaoSolidesDP.Worker/Dockerfile -t integracao-solidesdp .
#   docker run -d --name integracao-solidesdp --restart unless-stopped \
#     -e ConnectionStrings__Rhu="Server=...;Database=bd_rhu_adn;..." -e SolidesDP__Token="..." \
#     -e Sync__DryRun=false -e Sync__GoLiveDate=2026-11-01 \
#     -v integracao-solidesdp-reports:/app/reports integracao-solidesdp
#
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=1.0.0
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/IntegracaoSolidesDP.Worker/IntegracaoSolidesDP.Worker.csproj src/IntegracaoSolidesDP.Worker/packages.lock.json src/IntegracaoSolidesDP.Worker/
RUN dotnet restore src/IntegracaoSolidesDP.Worker/IntegracaoSolidesDP.Worker.csproj --locked-mode
COPY src/IntegracaoSolidesDP.Worker/ src/IntegracaoSolidesDP.Worker/
RUN dotnet publish src/IntegracaoSolidesDP.Worker/IntegracaoSolidesDP.Worker.csproj -c Release --no-restore \
      -o /app/publish -p:UseAppHost=false -p:Version=$VERSION \
 && rm -f /app/publish/appsettings.Development.json

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
LABEL org.opencontainers.image.source="https://github.com/rhsenso/IntegracaoSolidesDP" \
      org.opencontainers.image.description="Integração RHSenso -> Sólides DP (worker)"
# tzdata: Execution:TimeZone (America/Bahia) precisa do banco de fusos do sistema.
RUN apt-get update \
 && apt-get install -y --no-install-recommends tzdata \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish ./
RUN mkdir -p /app/logs /app/reports && chown -R $APP_UID /app/logs /app/reports
# TZ só afeta o horário dos logs; o agendamento usa Execution:TimeZone.
ENV DOTNET_ENVIRONMENT=Production \
    TZ=America/Bahia
USER $APP_UID
VOLUME ["/app/reports", "/app/logs"]
ENTRYPOINT ["dotnet", "IntegracaoSolidesDP.dll"]
