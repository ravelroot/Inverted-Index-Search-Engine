FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY .editorconfig global.json Directory.Build.props Directory.Packages.props ./
COPY src/Sonda.Analysis/Sonda.Analysis.csproj src/Sonda.Analysis/
COPY src/Sonda.Storage/Sonda.Storage.csproj src/Sonda.Storage/
COPY src/Sonda.Engine/Sonda.Engine.csproj src/Sonda.Engine/
COPY src/Sonda.Server/Sonda.Server.csproj src/Sonda.Server/
COPY src/Sonda.Cli/Sonda.Cli.csproj src/Sonda.Cli/
RUN dotnet restore src/Sonda.Server/Sonda.Server.csproj \
 && dotnet restore src/Sonda.Cli/Sonda.Cli.csproj
COPY src/ src/
RUN dotnet publish src/Sonda.Server/Sonda.Server.csproj -c Release -o /out/server --no-restore \
 && dotnet publish src/Sonda.Cli/Sonda.Cli.csproj -c Release -o /out/cli --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENV Urls=http://+:8080 \
    SONDA_BASE=/dados \
    Sonda__IndexPath=/dados/indice \
    Sonda__DataPath=/dados \
    Sonda__AllowedRoots__0=/documentos \
    Sonda__Sources__0__Name=documentos \
    Sonda__Sources__0__Path=/documentos \
    DOTNET_gcServer=1
COPY --from=build /out/server .
COPY --from=build /out/cli ./cli
RUN mkdir -p /dados /documentos && chown -R $APP_UID /dados
USER $APP_UID
VOLUME ["/dados"]
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=5 CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "Sonda.Server.dll"]
