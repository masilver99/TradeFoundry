FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG VERSION=0.0.0-dev
ARG REVISION=unknown
WORKDIR /src
COPY ["TradeFoundry.csproj", "."]
RUN dotnet restore "TradeFoundry.csproj"
COPY . .
RUN dotnet publish "TradeFoundry.csproj" -c Release -o /app/publish \
    /p:UseAppHost=false /p:Version=$VERSION /p:SourceRevisionId=$REVISION

FROM build AS test
RUN dotnet test tests/TradeFoundry.Tests/TradeFoundry.Tests.csproj \
    -c Release --nologo --maxcpucount:1 --artifacts-path /verification \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG VERSION=0.0.0-dev
ARG REVISION=unknown
LABEL org.opencontainers.image.title="TradeFoundry" \
      org.opencontainers.image.source="https://git.shaa.one/masilver/TradeFoundry" \
      org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.revision=$REVISION
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV Storage__DataDirectory=/app/data
ENV DataProtection__KeysDirectory=/app/data/keys
ENV Mcp__Enabled=false
COPY --from=build /app/publish .
RUN mkdir -p /app/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "TradeFoundry.dll"]
