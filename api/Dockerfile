FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY BdoMarketTracker.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .

# Railway provides PORT env var; default to 8080
EXPOSE 8080

ENTRYPOINT ["sh", "-c", "dotnet BdoMarketTracker.dll --urls http://+:${PORT:-8080}"]
