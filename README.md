<p align="center">
  <img src="https://spokes.sh/logo.png" alt="Spokes Logo" width="80" />
</p>

<h1 align="center">Spokes Server</h1>

<p align="center">
  <strong>Private, self-hosted chat, voice & video for your group.</strong><br>
  The simple, fully-featured alternative to Discord, Slack, and other cloud chat apps.
</p>

<p align="center">
  <a href="https://demo.spokes.sh">Live Demo</a> •
  <a href="https://spokes.sh/introduction">Documentation</a> •
  <a href="https://spokes.sh/pricing">Pricing</a> •
  <a href="https://forum.spokes.sh">Forum</a> •
  <a href="https://spokes.sh/releases">Release Notes</a>
</p>

---

## What is Spokes?

Spokes is an all-in-one, self-hosted communication platform built for friend groups, families, and small teams. Everything you expect from a modern chat app. Text channels, direct messages, voice calls, video calls and more, but running on **your** server, under **your** control.

All data stays on your server. Any data that must transit through external services (like mobile push notifications) is encrypted end-to-end.

## Features

| Feature | Description |
|---------|-------------|
| 💬 **Channels & DMs** | Organize conversations into dedicated channels or message users directly in private DMs |
| 📹 **Voice & Video** | Low-latency, high-quality audio and video channels hosted directly on your server |
| 🎙️ **Noise Cancellation** | Built-in noise cancelling for crystal clear voice chat, even in noisy environments |
| 📱 **Mobile Apps** | Free native [iOS](https://apps.apple.com/us/app/spokes/id6763387907) and [Android](https://play.google.com/store/apps/details?id=com.pcbee.spokes) apps with reliable push notifications |
| 🔒 **Encrypted Notifications** | Push notifications are delivered via Apple/Google but encrypted — they can't read the content |
| 🛡️ **Secure Channels** | Optional encryption on specific channels to protect sensitive conversations |
| 📸 **Photo Albums** | Shared photo albums for private, organized picture sharing with your group |
| 📅 **Event Calendar** | Built-in calendar for event planning, availability tracking, and group coordination |
| 🎞️ **GIF Support** | Built-in GIF search integration for expressive conversations |
| 🐳 **Single Container** | Deploy the entire platform — chat, voice server, database, and all features — with one Docker container |

## Quick Start

Spokes server package url : `ghcr.io/pcbeeqc/spokes:latest`

**Prerequisites:**
- A Linux host with Docker and Docker Compose installed
- Ports 80, 443, 7881, and 30000-30499 available and forwarded
- A domain name (e.g., `spokes.yourdomain.com`) pointing to your server's public IP

```yaml
version: '3.8'

services:
  spokes:
    image: ghcr.io/pcbeeqc/spokes:latest
    container_name: spokes_server
    restart: unless-stopped
    ports:
      # Web UI and API (Can be mapped to any host port, e.g., "80:8080")
      - "8080:8080"

      # LiveKit TCP Fallback (Must be mapped 1-to-1)
      - "7881:7881"

      # LiveKit UDP Range (Must be mapped 1-to-1)
      - "30000-30499:30000-30499/udp"
    volumes:
      # Persistent data storage (databases, configuration, uploads)
      - ./spokes-data:/data
    environment:
      # Required: The master password used to encrypt secure channels escrow
      - SPOKES_MASTER_PASSWORD=YourSecureMasterPasswordHere
```

For platform-specific guides, see the [Deployment Documentation](https://spokes.sh/deployments/self-hosted/unraid/).

## Design Principles

- **Absolute Privacy**: All data stays on your server. Any data that must transit through external services (like mobile push notifications) is encrypted in transit.
- **Frictionless Experience**: Spokes aims to be as easy to use as possible for your friends and family. With native mobile apps, reliable notifications, GIF support and more, Spokes goal is to "just work".
- **Easy to Self-Host**: Deploying and maintaining Spokes should be as easy as possible for the server administrator, which is why everything runs from a single all-in-one container.

## Licensing

Spokes Server is source-available under the [Business Source License 1.1](LICENSE.md).

- **Free to Host**: Deploy and run your server forever for free.
- **Paid Push Relay**: Native push notifications for iOS and Android require a [$45/year Spokes license](https://spokes.sh/pricing) to access the Spokes Push Relay. PWA web notifications are always free.
- **No per-user fees**: One flat price per server for push notifications — unlimited users, unlimited channels.
- **Support the project**: Purchasing a Spokes license directly funds the continued development of the platform.

## Building from Source

```bash
# Prerequisites: .NET 9 SDK
dotnet restore Spokes_Server/Spokes_Server.csproj
dotnet build Spokes_Server/Spokes_Server.csproj -c Release

# Or build the Docker image
docker build -t spokes-server .
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## Security

To report a vulnerability, see [SECURITY.md](SECURITY.md). **Do not open a public issue for security vulnerabilities.**

## Links

- 🌐 [Website](https://spokes.sh)
- 🎮 [Live Demo](https://demo.spokes.sh)
- 📖 [Documentation](https://spokes.sh/introduction)
- 💬 [Forum](https://forum.spokes.sh)
- 📋 [Release Notes](https://spokes.sh/releases)

---

<p align="center">
  © 2026 Logiciel PCBee inc.
</p>
