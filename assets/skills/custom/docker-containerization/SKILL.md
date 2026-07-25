---
name: docker-containerization
description: Standards for creating production-ready Docker containers — multi-stage builds, non-root user execution, minimal base images (Alpine/Distroless), layer caching optimization, environment variable configuration, `.dockerignore` usage, and container health checks. Use when creating Dockerfiles, writing docker-compose files, or containerizing applications.
category: Development & Architecture
author: Klydis Custom
version: 1.0.0
---

# Docker Containerization

Containerizing applications ensures consistency across development, staging, and production environments. Production Docker images should be minimal, secure, fast to build, and stateless.

## Multi-Stage Build Pattern

Always separate the build toolchain from the runtime image to reduce attack surface and shrink image size.

```dockerfile
# Stage 1: Build environment
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["MyApp.csproj", "./"]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime environment
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Run as non-root user for security
RUN adduser --disabled-password --gecos "" appuser && chown -R appuser:appuser /app
USER appuser

COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "MyApp.dll"]
```

## Dockerfile Best Practices

1. **Use Minimal Base Images**: Prefer `distroless`, `alpine`, or slim official images over full OS images.
2. **Optimize Layer Caching**: Copy dependency manifests (`package.json`, `.csproj`, `go.mod`) and restore dependencies BEFORE copying full application source code.
3. **Never Run as Root**: Explicitly add and switch to a non-root `USER` before `ENTRYPOINT`.
4. **Use `.dockerignore`**: Exclude `.git`, `node_modules`, `bin/`, `obj/`, `.env`, and secret files from container context.
5. **Implement HEALTHCHECK**:
   ```dockerfile
   HEALTHCHECK --interval=30s --timeout=3s --retries=3 \
     CMD curl -f http://localhost:8080/health || exit 1
   ```
6. **Graceful Signal Handling**: Ensure application listens for `SIGTERM` and shuts down cleanly within the grace period.

## Checklist

- [ ] Multi-stage builds separate build tools from runtime image
- [ ] Container runs under non-root user
- [ ] `.dockerignore` excludes node_modules, build outputs, and `.env` files
- [ ] Layer cache optimized by copying lockfiles first
- [ ] Healthcheck defined and tested
