# Simulator image, run as the job-simulator Container Apps job (ADR-024). Built
# from the repository root so the shared build files are in the context:
#
#   az acr build --registry <registry> --image simulator:<tag> \
#       --file src/Touchline.Simulator/Dockerfile .
#
# The same layout as the Match Centre's Dockerfile, which explains the single
# source COPY (ACR Tasks uses Docker's classic builder).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/Touchline.Domain/Touchline.Domain.csproj src/Touchline.Domain/
COPY src/Touchline.ServiceDefaults/Touchline.ServiceDefaults.csproj src/Touchline.ServiceDefaults/
COPY src/Touchline.Simulator/Touchline.Simulator.csproj src/Touchline.Simulator/
RUN dotnet restore src/Touchline.Simulator/Touchline.Simulator.csproj

COPY src/ src/
RUN dotnet publish src/Touchline.Simulator/Touchline.Simulator.csproj \
    --configuration Release --no-restore --output /app

# The ASP.NET Core runtime rather than the plain one, because the service
# defaults reference the ASP.NET Core shared framework. Nothing listens: the
# job publishes events and exits.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Touchline.Simulator.dll"]
