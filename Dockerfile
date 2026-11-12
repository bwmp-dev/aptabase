# Server Build
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS server
WORKDIR /work/src

COPY ./src/Aptabase.csproj /work/src

ARG TARGETARCH
RUN dotnet restore "./Aptabase.csproj" -a $TARGETARCH

COPY ./etc/clickhouse /work/etc/clickhouse
COPY ./etc/geoip /work/etc/geoip
COPY ./src /work/src

RUN dotnet publish "Aptabase.csproj" -m:1 -a $TARGETARCH -c Release -o /work/publish /p:UseAppHost=false

# WebApp Build
FROM node:22 AS webapp
WORKDIR /work

COPY ./src/package.json ./src/package-lock.json ./
RUN npm ci

COPY ./src ./
RUN npm run build

# Final
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15@sha256:bfd912c83295ce66d4b706ebb8541b9f14259b0e76fbb88b2751f58ce2399d2e AS final
WORKDIR /app

COPY --from=server /work/publish .
COPY --from=webapp /work/wwwroot ./wwwroot
COPY LICENSE .

ENTRYPOINT ["dotnet", "Aptabase.dll"]
