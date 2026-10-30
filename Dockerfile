FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Reon.S3.Server.slnx ./
COPY src/ src/

RUN dotnet restore src/Reon.Api/Reon.Api.csproj

# Профиль публикации MSBuild из src/Reon.Api/Properties/PublishProfiles
# (имя файла без расширения .pubxml). Пустое значение — обычная
# Release-публикация без профиля.
ARG PUBLISH_PROFILE=
RUN if [ -n "$PUBLISH_PROFILE" ]; then \
        dotnet publish src/Reon.Api/Reon.Api.csproj -c Release -o /app/publish \
            /p:UseAppHost=false -p:PublishProfile="$PUBLISH_PROFILE"; \
    else \
        dotnet publish src/Reon.Api/Reon.Api.csproj -c Release -o /app/publish \
            /p:UseAppHost=false; \
    fi

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

USER app
ENTRYPOINT ["dotnet", "Reon.Api.dll"]
