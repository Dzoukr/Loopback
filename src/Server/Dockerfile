# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build

# Copy solution and shared build configuration files
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY global.json ./
COPY Loopback.sln ./

# Restore packages
COPY src/Server/Server.fsproj ./src/Server/
RUN dotnet restore src/Server/Server.fsproj

# Build and publish
COPY src/Server/ ./src/Server/
RUN dotnet publish src/Server/Server.fsproj -c Release -o /app/publish /p:UseAppHost=false --no-restore

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080

# Copy the published app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Server.dll"]
