FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["Cli/Cli.csproj", "Cli/"]
COPY ["Common/Common.csproj", "Common/"]
RUN dotnet restore "Cli/Cli.csproj"

COPY . .

WORKDIR "/src/Cli"
RUN dotnet build "Cli.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Cli.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends libkrb5-3 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=publish /app/publish .
COPY ["Api/Migrations", "Migrations"]
COPY ["contracts", "contracts"]
COPY ["Cli/entrypoint.sh", "entrypoint.sh"]
RUN chmod +x entrypoint.sh
ENTRYPOINT ["./entrypoint.sh"]