# ══════════════════════════════════════════════════════════════════════════════
# Multi-Stage Dockerfile for VectorCatalog.Api (.NET 8)
# Optimized for layer caching and small final image size
# ══════════════════════════════════════════════════════════════════════════════

# ── Stage 1: Build application ────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy all source code
COPY . .

# Restore and build in Release configuration
WORKDIR "/src/src/VectorCatalog.Api"
RUN dotnet restore "VectorCatalog.Api.csproj"
RUN dotnet build "VectorCatalog.Api.csproj" -c Release -o /app/build

# ── Stage 2: Publish application ──────────────────────────────────────────────
FROM build AS publish
RUN dotnet publish "VectorCatalog.Api.csproj" -c Release -o /app/publish

# ── Stage 3: Final runtime image ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Create non-root user for security
RUN groupadd -r appuser && useradd -r -g appuser appuser

WORKDIR /app

# Copy published application
COPY --from=publish /app/publish .

# Set ownership
RUN chown -R appuser:appuser /app

# Switch to non-root user
USER appuser

# Expose HTTP port (API + /metrics endpoint)
EXPOSE 8080

# Environment variables (can be overridden at runtime)
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Health check (calls liveness endpoint)
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "VectorCatalog.Api.dll"]
