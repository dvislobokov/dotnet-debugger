# Test environment for Linux: SDK 10 (builds everything, runtime 10) plus SDK/runtime 9 (the adapter and the tests target net9.0).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS sdk9
FROM mcr.microsoft.com/dotnet/sdk:10.0
COPY --from=sdk9 /usr/share/dotnet /usr/share/dotnet
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_UI_LANGUAGE=en
WORKDIR /work
