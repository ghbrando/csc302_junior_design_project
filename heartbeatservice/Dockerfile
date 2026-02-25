FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore (layer-cached until project files change)
COPY unicore.shared/unicore.shared.csproj unicore.shared/
COPY heartbeatservice/heartbeatservice.csproj heartbeatservice/
RUN dotnet restore heartbeatservice/heartbeatservice.csproj

# Copy source and publish
COPY unicore.shared/ unicore.shared/
COPY heartbeatservice/ heartbeatservice/
RUN dotnet publish heartbeatservice/heartbeatservice.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Cloud Run injects $PORT (default 8080); ASP.NET Core reads ASPNETCORE_HTTP_PORTS
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "heartbeatservice.dll"]
