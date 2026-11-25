# Spokes Server - Self-Hosted Discord Alternative

**Private, self-hosted chat, voice, video & photo albums in a single Docker container.**  
The easy-to-host Discord alternative with official iOS and Android mobile apps and working push notifications.

[Live Demo](https://demo.spokes.sh) • [Documentation](https://spokes.sh/introduction/) • [Compare Alternatives](https://github.com/PCBeeQC/self-hosted-discord-alternatives) • [Pricing](https://spokes.sh/pricing) • [Forum](https://forum.spokes.sh) • [Release Notes](https://spokes.sh/releases)

---

## What is Spokes?

**Spokes** ([spokes.sh](https://spokes.sh)) is an all-in-one **self-hosted Discord alternative** built for friend groups, families, gaming communities, and small teams. It gives you organized text channels, direct messages, persistent drop-in voice and video rooms, shared photo albums, and a group calendar, all running on **your** server in a **single Docker container**.

Unlike most open-source Discord clones that only work in a desktop browser or require 5 to 10 containers, Spokes includes **official iOS (App Store) and Android (Google Play) mobile apps** with reliable, end-to-end encrypted push notifications and an embedded LiveKit voice and video server out of the box.

## Features

| Feature | Description |
| :--- | :--- |
| **Channels & DMs** | Organize conversations into dedicated channels and categories or message users in private DMs and group chats |
| **Voice & Video Channels** | Persistent Discord-style drop-in voice rooms, video calls, and screen sharing (embedded LiveKit server) |
| **Noise Cancellation** | Built-in local noise cancellation for clear voice chat |
| **Official Mobile Apps** | Free native [iOS (App Store)](https://apps.apple.com/us/app/spokes/id6763387907) and [Android (Google Play)](https://play.google.com/store/apps/details?id=com.pcbee.spokes) apps with working push notifications |
| **Encrypted Notifications** | Push notification payloads are encrypted locally by your server before transit through the Spokes Relay and Apple APNs / Google FCM |
| **Secure Channels** | Optional password-locked vault encryption on private channels and DMs to protect sensitive conversations at rest |
| **Built-in SSO (OIDC)** | Includes an embedded Casdoor identity provider out of the box, plus support for external OIDC providers (Authentik, Pocket ID, Authelia) |
| **Photo Albums** | Shared photo albums for private, organized picture and video sharing with your group |
| **Event Calendar** | Built-in calendar for event planning, availability tracking, and group coordination |
| **GIF Support** | Built-in GIF search integration |
| **Single Container** | Deploy the entire platform (chat server, LiveKit voice server, OIDC identity, and JSON file storage) in one Docker container with no external database required |

## Quick Start

Spokes is available on both Docker Hub and GitHub Container Registry :
- `pcbeeqc/spokes:latest`
- `ghcr.io/pcbeeqc/spokes:latest`

**Prerequisites:**
- A Linux host (or Unraid, TrueNAS, Synology, CasaOS, Portainer, Proxmox) with Docker installed
- Ports 80, 443, 7881, and 30000-30007 available and forwarded
- A domain name (e.g., `spokes.yourdomain.com`) pointing to your server's public IP

```yaml
services:
  spokes:
    image: pcbeeqc/spokes:latest
    container_name: spokes_server
    restart: unless-stopped
    ports:
      # Web UI and API (Can be mapped to any host port, e.g., "80:8080")
      - "8080:8080"

      # LiveKit TCP Fallback (Must be mapped 1-to-1)
      - "7881:7881"

      # LiveKit UDP Mux Range (Must be mapped 1-to-1)
      - "30000-30007:30000-30007/udp"
    volumes:
      # Persistent data storage (databases, configuration, uploads)
      - ./spokes-data:/data
    environment:
      # Required: Master password used to encrypt server secrets, backups, and secure channel escrow
      - SPOKES_MASTER_PASSWORD=YourSecureMasterPasswordHere
```

For platform-specific guides (Unraid, TrueNAS, CasaOS, Portainer, Docker Compose, Nginx Proxy Manager, Cloudflare Tunnels), see the [Deployment Documentation](https://spokes.sh/introduction/).

## Design Principles

- **Complete Data Ownership**: All data stays on your server in a single `/data` folder. Any data that transits through external services (like mobile push notifications) is encrypted before leaving your server.
- **Frictionless for Real-Life Groups**: Spokes is built so non-technical friends and family members can download the official app from the App Store or Google Play, log in, and get reliable notifications right away.
- **Simple to Host and Maintain**: Everything runs in a single Docker container with simple JSON file storage, so there is no external PostgreSQL, MongoDB, or Redis database to maintain or corrupt during updates.

Read more about why Spokes is built this way : [What you actually need in a self-hosted Discord alternative (Matrix vs. Mattermost vs. Spokes)](https://dev.to/matsmd/what-you-actually-need-in-a-self-hosted-discord-alternative-matrix-vs-mattermost-vs-spokes-5fmj).

## Licensing & Pricing

Spokes Server is source-available under the [Business Source License 1.1](https://github.com/PCBeeQC/Spokes_Server/blob/main/LICENSE.md), converting to Apache 2.0 after four years.

- **Free Self-Hosted Server**: Spokes is free to deploy and run on your own hardware with unlimited users and channels.
- **Web Push Included**: Browser and desktop PWA push notifications are built-in and completely free.
- **2-Month Free Mobile Trial**: Every new server deployment includes 2 months of native mobile push notifications with no credit card required.
- **[Spokes Pass](https://spokes.sh/pricing)**: Delivering push notifications to native iOS and Android apps requires external relay infrastructure. The Spokes Pass unlocks ongoing native mobile push notifications for your server while supporting development:
  - **1-Year Pass**: $45 per server
  - **2-Year Pass**: $70 per server
  - **Lifetime Pass**: $385 per server
- **Unlimited Members**: One pass covers your entire server instance. There are no per-user fees and never will be.

## Links

- [Website](https://spokes.sh)
- [Live Demo](https://demo.spokes.sh)
- [Documentation](https://spokes.sh/introduction)
- [Source Code (GitHub)](https://github.com/PCBeeQC/Spokes_Server)
- [Self-Hosted Discord Alternatives Comparison Repo (GitHub)](https://github.com/PCBeeQC/self-hosted-discord-alternatives)
- [Self-Hosted Discord Alternatives Compared](https://spokes.sh/compare/)
- [Spokes vs. Matrix + Element](https://spokes.sh/compare/matrix-element/)
- [Spokes vs. Mattermost, Rocket.Chat & Zulip](https://spokes.sh/compare/mattermost-rocketchat/)
- [Spokes vs. Stoat (Revolt), Fluxer & Spacebar](https://spokes.sh/compare/stoat-revolt/)
