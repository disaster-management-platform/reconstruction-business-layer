# syntax=docker/dockerfile:1.7
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.403-noble
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.14-noble
FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY NuGet.Config ./
COPY ["Reconstruction.Api/Reconstruction.Api.csproj", "Reconstruction.Api/"]
COPY ["Reconstruction.Application/Reconstruction.Application.csproj", "Reconstruction.Application/"]
COPY ["Reconstruction.Domain/Reconstruction.Domain.csproj", "Reconstruction.Domain/"]
COPY ["Reconstruction.Infrastructure/Reconstruction.Infrastructure.csproj", "Reconstruction.Infrastructure/"]
RUN --mount=type=secret,id=nuget_credentials,required=true \
    NuGetPackageSourceCredentials_GitHubPackages="$(cat /run/secrets/nuget_credentials)" \
    dotnet restore "Reconstruction.Api/Reconstruction.Api.csproj" --configfile NuGet.Config
COPY Reconstruction.Api/ Reconstruction.Api/
COPY Reconstruction.Application/ Reconstruction.Application/
COPY Reconstruction.Domain/ Reconstruction.Domain/
COPY Reconstruction.Infrastructure/ Reconstruction.Infrastructure/
RUN dotnet publish "Reconstruction.Api/Reconstruction.Api.csproj" -c Release --no-restore -o /out /p:UseAppHost=false
FROM ${RUNTIME_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /out/ ./
ENV ASPNETCORE_HTTP_PORTS=8080 ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Reconstruction.Api.dll"]
