# Build container
FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine3.16 AS build-alpine

ARG Configuration=Release
ARG TelemetryType=TELEMETRY_DEVELOPMENT
ARG MindaroBuildNumber=0.0

WORKDIR /src/devhostagent
COPY /src/devhostagent/devhostAgent.csproj .
COPY /src/common/common.csproj /src/common/
RUN dotnet restore
COPY /src/devhostagent/ /src/devhostagent/
COPY /src/common/ /src/common/
COPY /build/ /build/
ENV TelemetryType=${TelemetryType}
ENV MINDARO_BUILD_NUMBER=${MindaroBuildNumber}

RUN dotnet publish -c ${Configuration} -o /src/publish devhostAgent.csproj

# Final container
FROM mcr.microsoft.com/dotnet/aspnet:6.0-alpine3.16 as final
WORKDIR /src/devhostagent
COPY --from=build-alpine /src/publish /src/devhostagent
ENTRYPOINT ["dotnet", "/src/devhostagent/Microsoft.BridgeToKubernetes.DevHostAgent.dll"]