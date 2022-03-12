FROM mcr.microsoft.com/dotnet/sdk:6.0 as build
WORKDIR /build
COPY Payments.csproj Payments.csproj
RUN dotnet restore
COPY . .
RUN dotnet publish -c release

FROM mcr.microsoft.com/dotnet/aspnet:6.0
WORKDIR /app

COPY --from=build /build/bin/release/net6.0/publish/ .

ENV ASPNETCORE_URLS=http://+:8000

RUN useradd --uid $(shuf -i 2000-65000 -n 1) app
USER app

ENTRYPOINT ["dotnet", "Payments.dll", "--hostBuilder:reloadConfigOnChange=false"]
