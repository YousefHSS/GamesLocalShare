# Games Local Share - Cross-Platform Avalonia UI Application
# Dockerfile for building and running the application in a container

# =============================================================================
# BUILD STAGE
# =============================================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy project file and restore dependencies
COPY ["GamesLocalShare.csproj", "."]
RUN dotnet restore "./GamesLocalShare.csproj"

# Copy all source files
COPY . .

# Build the application
WORKDIR "/src/."
RUN dotnet build "./GamesLocalShare.csproj" -c $BUILD_CONFIGURATION -o /app/build

# =============================================================================
# PUBLISH STAGE
# =============================================================================
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./GamesLocalShare.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=true

# =============================================================================
# RUNTIME STAGE (for headless/server scenarios)
# =============================================================================
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
COPY --from=publish /app/publish .

# Expose ports for network discovery and file transfer
EXPOSE 45677/udp
EXPOSE 45678/tcp
EXPOSE 45679/tcp

# Note: This container is primarily for building/CI purposes.
# For running the GUI application, you'll need to run natively or use X11 forwarding.
#
# To run with X11 forwarding on Linux:
#   docker run -e DISPLAY=$DISPLAY -v /tmp/.X11-unix:/tmp/.X11-unix gameslocalshare
#
# For production use, we recommend running the application natively:
#   - Windows: Download the Windows release
#   - Linux: Download the Linux release  
#   - macOS: Download the macOS release

ENTRYPOINT ["dotnet", "GamesLocalShare.dll"]

# =============================================================================
# ALTERNATIVE: Linux Desktop Stage (with X11 dependencies)
# =============================================================================
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS linux-desktop

# Install X11 and font dependencies for running GUI in container
RUN apt-get update && apt-get install -y \
    libx11-6 \
    libxrandr2 \
    libxinerama1 \
    libxcursor1 \
    libxi6 \
    libgl1 \
    libfontconfig1 \
    fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=publish /app/publish .

# Expose ports
EXPOSE 45677/udp
EXPOSE 45678/tcp
EXPOSE 45679/tcp

ENTRYPOINT ["dotnet", "GamesLocalShare.dll"]