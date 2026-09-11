# Specify explicit versions for external dependencies so they can be tested and updated manually
ARG LIVEKIT_VERSION=v1.11.0
ARG CASDOOR_VERSION=3.49.0
ARG APP_VERSION=dev
ARG SPOKES_HMAC_SALT=default-development-salt
ARG SPOKES_KLIPY_KEY=

FROM livekit/livekit-server:${LIVEKIT_VERSION} AS livekit
FROM casbin/casdoor:${CASDOOR_VERSION} AS casdoor

# Use the official .NET SDK image to build the app
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy the project file and restore dependencies
COPY ["Spokes_Server/Spokes_Server.csproj", "Spokes_Server/"]
RUN dotnet restore "Spokes_Server/Spokes_Server.csproj"

# Copy the rest of the source code
COPY . .
WORKDIR "/src/Spokes_Server"



# Publish the application
FROM build AS publish
ARG APP_VERSION
RUN dotnet publish "Spokes_Server.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:InformationalVersion=${APP_VERSION}

# Install and run Obfuscar
RUN dotnet tool install --global Obfuscar.GlobalTool
ENV PATH="$PATH:/root/.dotnet/tools"
RUN sed -i 's|</Obfuscator>||' obfuscar.xml && \
    find /usr/share/dotnet/shared -mindepth 2 -maxdepth 2 -type d -exec echo '<AssemblySearchPath path="{}" />' \; >> obfuscar.xml && \
    echo '</Obfuscator>' >> obfuscar.xml
RUN obfuscar.console obfuscar.xml
RUN cp /app/publish/Obfuscated/Spokes_Server.dll /app/publish/Spokes_Server.dll
RUN rm -rf /app/publish/Obfuscated

# Use the official .NET ASP.NET runtime image for the final image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final

# Install Chromium dependencies for PuppeteerSharp PDF generation
RUN apt-get update && apt-get install -y --no-install-recommends \
    libnss3 \
    libnspr4 \
    libatk1.0-0 \
    libatk-bridge2.0-0 \
    libcups2 \
    libdrm2 \
    libxkbcommon0 \
    libxcomposite1 \
    libxdamage1 \
    libxfixes3 \
    libxrandr2 \
    libgbm1 \
    libasound2 \
    libpango-1.0-0 \
    libcairo2 \
    fonts-liberation \
    supervisor \
    curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV DataPath="/data"
ARG SPOKES_HMAC_SALT
ARG SPOKES_KLIPY_KEY
ENV SPOKES_HMAC_SALT=${SPOKES_HMAC_SALT}
ENV SPOKES_KLIPY_KEY=${SPOKES_KLIPY_KEY}
COPY --from=publish /app/publish .

# Copy LiveKit Server binary from the specific version image
COPY --from=livekit /livekit-server .

# Copy Casdoor binary from the specific version image
COPY --from=casdoor / /app/casdoor

# Copy Supervisor configuration and entrypoint script
COPY supervisord.conf .
COPY entrypoint.sh .
COPY wan_ip_monitor.sh .
RUN chmod +x entrypoint.sh wan_ip_monitor.sh

ENTRYPOINT ["/bin/bash", "/app/entrypoint.sh"]
