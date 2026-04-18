FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file and restore (layer-cached until project file changes)
COPY UniCoreLanding.csproj .
RUN dotnet restore UniCoreLanding.csproj

# Copy source and publish
COPY . .
RUN dotnet publish UniCoreLanding.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Cloud Run injects $PORT (default 8080); ASP.NET Core reads ASPNETCORE_HTTP_PORTS
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "UniCoreLanding.dll"]
