# Match Centre image. Built from the repository root so the shared build files
# (Directory.Build.props, Directory.Packages.props) are in the context:
#
#   az acr build --registry <registry> --image matchcentre:<tag> \
#       --file src/Touchline.MatchCentre/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from project files only, so the restore layer is cached
# until a project file or package version changes.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Touchline.Domain/Touchline.Domain.csproj src/Touchline.Domain/
COPY src/Touchline.ServiceDefaults/Touchline.ServiceDefaults.csproj src/Touchline.ServiceDefaults/
COPY src/Touchline.Storage/Touchline.Storage.csproj src/Touchline.Storage/
COPY src/Touchline.MatchCentre/Touchline.MatchCentre.csproj src/Touchline.MatchCentre/
RUN dotnet restore src/Touchline.MatchCentre/Touchline.MatchCentre.csproj

# One COPY for all the source. Separate per-project COPYs can produce an empty
# layer (a project that holds only its .csproj), and Docker's classic builder,
# which ACR Tasks uses, then fails with "layer does not exist". The classic
# builder can't copy several folders in one COPY without flattening them
# (COPY --parents needs BuildKit), so this also brings in the other projects'
# source. That costs a few KB of upload and nothing at run time, because only
# the published output reaches the final image.
COPY src/ src/
RUN dotnet publish src/Touchline.MatchCentre/Touchline.MatchCentre.csproj \
    --configuration Release --no-restore --output /app

# The runtime image has no SDK, listens on 8080 (ASPNETCORE_HTTP_PORTS) and
# runs as the built-in non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Touchline.MatchCentre.dll"]
