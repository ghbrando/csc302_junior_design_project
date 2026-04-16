FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY landingpage/UniCoreLanding.csproj landingpage/
RUN dotnet restore landingpage/UniCoreLanding.csproj
COPY landingpage/ landingpage/
RUN dotnet publish landingpage/UniCoreLanding.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "UniCoreLanding.dll"]
