
FROM mcr.microsoft.com/dotnet/sdk:8.0
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_UI_LANGUAGE=en
WORKDIR /work

# roslyn-language-server targets net10.0, so it needs the .NET 10 runtime next to the 8.0 SDK.
# Only the shared runtime is copied, `dotnet build`/`dotnet test` keep using the 8.0 SDK.
COPY --from=mcr.microsoft.com/dotnet/runtime:10.0 /usr/share/dotnet/shared/Microsoft.NETCore.App /usr/share/dotnet/shared/Microsoft.NETCore.App
COPY --from=mcr.microsoft.com/dotnet/runtime:10.0 /usr/share/dotnet/host/fxr /usr/share/dotnet/host/fxr

# The tool is published as a RID-specific tool package, which `dotnet tool install` of the 8.0 SDK
# can't handle (it looks for tools/<tfm>/any/DotnetToolSettings.xml), so unpack the nupkg manually.
ARG ROSLYN_LS_VERSION=5.12.0-1.26426.8
RUN apt-get update && apt-get install -y --no-install-recommends unzip && rm -rf /var/lib/apt/lists/* \
    && curl -fsSL -o /tmp/roslyn-ls.nupkg "https://api.nuget.org/v3-flatcontainer/roslyn-language-server.linux-x64/${ROSLYN_LS_VERSION}/roslyn-language-server.linux-x64.${ROSLYN_LS_VERSION}.nupkg" \
    && unzip -q /tmp/roslyn-ls.nupkg 'tools/net10.0/linux-x64/*' -d /tmp/roslyn-ls \
    && mv /tmp/roslyn-ls/tools/net10.0/linux-x64 /opt/roslyn-language-server \
    && chmod +x /opt/roslyn-language-server/roslyn-language-server \
    && ln -s /opt/roslyn-language-server/roslyn-language-server /usr/local/bin/roslyn-language-server \
    && rm -rf /tmp/roslyn-ls /tmp/roslyn-ls.nupkg
RUN roslyn-language-server --help
