FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src ./src
RUN dotnet publish src/CheckDraw.Api -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT dotnet CheckDraw.Api.dll --urls http://0.0.0.0:${PORT:-8080}
