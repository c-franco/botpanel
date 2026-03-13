# ─── Build Stage ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY BotPanel.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# ─── Runtime Stage ────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Install Docker CLI so we can manage containers from inside
RUN apt-get update && apt-get install -y docker.io && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Non-root user for security
RUN groupadd -r botpanel && useradd -r -g botpanel botpanel
# Needs docker group to access socket
RUN usermod -aG docker botpanel

USER botpanel

EXPOSE 5000
ENTRYPOINT ["dotnet", "BotPanel.dll"]
