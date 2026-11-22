#!/bin/sh
set -e
dotnet build -c Release src/GpBattleshipWebapp
dotnet publish -c Release src/GpBattleshipWebapp
# Build for linux/amd64 and push directly to GitHub Container Registry.
# The Dockerfile lives at the repo root, but the build context must be the
# project folder (it contains GpBattleshipWebapp.csproj). -f points at the root
# Dockerfile while the context (final arg) is the project dir — matches
# docker-compose.yml (context: ./src/GpBattleshipWebapp, dockerfile: ../../Dockerfile).
docker buildx build --platform linux/amd64 \
  -f Dockerfile \
  -t ghcr.io/gmphan/gp-battleship-webapp:latest \
  --push \
  ./src/GpBattleshipWebapp
#build for multi-platform image and push directly to Azure Container Registry
#docker buildx build --platform linux/amd64,linux/arm64 -t ocbuuregistry.azurecr.io/gp-battleship-webapp:latest --push .
