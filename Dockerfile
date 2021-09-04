FROM mcr.microsoft.com/dotnet/sdk:5.0 as build
WORKDIR /build
COPY Payments.csproj Payments.csproj
RUN dotnet restore
COPY . .
RUN dotnet publish -c release

FROM mcr.microsoft.com/dotnet/aspnet:5.0
WORKDIR /app

COPY --from=build /build/bin/release/net5.0/publish/ .
RUN mkdir -p ah/files

ENTRYPOINT ["dotnet", "Payments.dll"]

VOLUME /data
