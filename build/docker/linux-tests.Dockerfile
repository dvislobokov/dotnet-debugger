# Test environment for Linux: SDK 10 builds everything; the runtimes 8, 9 and 10 are all there. Everything targets net8.0
# and rolls forward, so with runtime 8 present the adapter, the tests and the debuggees really run on .NET 8 - the
# configuration of a machine where nothing newer may be installed.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime8
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime9
FROM mcr.microsoft.com/dotnet/sdk:10.0
COPY --from=runtime8 /usr/share/dotnet/shared /usr/share/dotnet/shared
COPY --from=runtime9 /usr/share/dotnet/shared /usr/share/dotnet/shared
ENV DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_UI_LANGUAGE=en
WORKDIR /work
