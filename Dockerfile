FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY VitallyMcp.sln ./
COPY VitallyMcp/*.csproj VitallyMcp/
COPY VitallyMcp.Tests/*.csproj VitallyMcp.Tests/
RUN dotnet restore VitallyMcp/VitallyMcp.csproj

COPY VitallyMcp/. VitallyMcp/
RUN dotnet publish VitallyMcp/VitallyMcp.csproj -c Release -o /out --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /out ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "VitallyMcp.dll"]
