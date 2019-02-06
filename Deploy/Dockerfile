FROM microsoft/dotnet:2.2-runtime AS base
# Install docker
# need to actually do this
WORKDIR /app

FROM threax/build AS build
WORKDIR /src
COPY . .
WORKDIR /src/Deploy
RUN dotnet restore Deploy.csproj
RUN dotnet build Deploy.csproj -c Release -o /app

FROM build AS publish
RUN dotnet publish Deploy.csproj -c Release -o /app

FROM base AS final
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /app
COPY --from=publish /app .
ENTRYPOINT ["dotnet", "Deploy.dll"]