# Builds the Windows installer on Linux (no Windows machine or VM needed):
#   1. publish the server and tray helper for win-x64 with the .NET SDK
#   2. compile installer/TradeFoundry.iss with Inno Setup running under Wine
#   3. leave TradeFoundry-Setup.exe alone in a scratch image for extraction
#
#   docker build -f installer/Dockerfile --target artifact --build-arg VERSION=1.2.0 -t tf-installer .
#   id=$(docker create tf-installer noop) && docker cp "$id:/TradeFoundry-Setup.exe" . && docker rm "$id"

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS publish
ARG VERSION=0.0.0-dev
ARG REVISION=unknown
ARG UPDATE_MANIFEST_URL=
WORKDIR /src
COPY ["TradeFoundry.csproj", "."]
COPY ["PublicJournal/Contracts/TradeFoundry.Publishing.Contracts.csproj", "PublicJournal/Contracts/"]
COPY ["DesktopShell/TradeFoundry.Desktop.csproj", "DesktopShell/"]
RUN dotnet restore TradeFoundry.csproj -r win-x64 \
 && dotnet restore DesktopShell/TradeFoundry.Desktop.csproj -r win-x64
COPY . .
# Building the apphost on Linux cannot embed the icon/manifest resources (warning NETSDK1074);
# the installer supplies the icon for shortcuts instead, and Avalonia sets DPI awareness itself.
RUN dotnet publish TradeFoundry.csproj -c Release -p:PublishProfile=WindowsDesktop \
      -p:PublishDir=/out/publish/ -p:Version="$VERSION" -p:SourceRevisionId="$REVISION" \
 && dotnet publish DesktopShell/TradeFoundry.Desktop.csproj -c Release -p:PublishProfile=WindowsDesktop \
      -p:PublishDir=/out/publish/ -p:Version="$VERSION" -p:SourceRevisionId="$REVISION" \
      -p:UpdateManifestUrl="$UPDATE_MANIFEST_URL"

# Inno Setup 6 under Wine. The image runs as the unprivileged user xclient in /work.
FROM amake/innosetup:innosetup6 AS installer
ARG VERSION=0.0.0-dev
COPY --chown=xclient:xusers installer/TradeFoundry.iss /work/installer/
COPY --chown=xclient:xusers --from=publish /out/publish /work/artifacts/windows/publish
RUN mkdir -p /work/artifacts/windows/installer \
 && iscc /Q "/DMyAppVersion=${VERSION}" "/DMyAppNumericVersion=${VERSION%%-*}" installer/TradeFoundry.iss

FROM scratch AS artifact
COPY --from=installer /work/artifacts/windows/installer/TradeFoundry-Setup.exe /
