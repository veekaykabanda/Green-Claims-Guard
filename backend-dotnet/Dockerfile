# The Green Claims Guard API. Build context is this folder (backend-dotnet).
#   docker build -t greenclaims-api backend-dotnet

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, on its own layer, so a code change does not download every package again.
COPY GreenClaimsGuard.Api/GreenClaimsGuard.Api.csproj GreenClaimsGuard.Api/
RUN dotnet restore GreenClaimsGuard.Api/GreenClaimsGuard.Api.csproj

COPY GreenClaimsGuard.Api/ GreenClaimsGuard.Api/
RUN dotnet publish GreenClaimsGuard.Api/GreenClaimsGuard.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Production on purpose: the API refuses to start without AUTH0_DOMAIN and AUTH0_AUDIENCE, and refuses data imports.
ENV ASPNETCORE_ENVIRONMENT=Production \
    Port=8080
EXPOSE 8080

# Not root. The .NET images ship an unprivileged "app" user.
USER $APP_UID
ENTRYPOINT ["dotnet", "GreenClaimsGuard.Api.dll"]
