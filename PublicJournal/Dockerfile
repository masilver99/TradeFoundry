FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Contracts/TradeFoundry.Publishing.Contracts.csproj Contracts/
COPY App/TradeFoundry.PublicJournal.csproj App/
RUN dotnet restore App/TradeFoundry.PublicJournal.csproj
COPY Contracts/ Contracts/
COPY App/ App/
RUN dotnet publish App/TradeFoundry.PublicJournal.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV Storage__DataDirectory=/app/data
COPY --from=build /out .
RUN mkdir -p /app/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "TradeFoundry.PublicJournal.dll"]
