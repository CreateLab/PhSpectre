FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY PhSpectre/PhSpectre.csproj             PhSpectre/
COPY PhSpectre.API/PhSpectre.API.csproj     PhSpectre.API/
RUN dotnet restore PhSpectre.API/PhSpectre.API.csproj

COPY PhSpectre/     PhSpectre/
COPY PhSpectre.API/ PhSpectre.API/
RUN dotnet publish PhSpectre.API/PhSpectre.API.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "PhSpectre.API.dll"]
