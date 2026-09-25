# Spokes Architecture Context

This file provides Spokes-specific architecture context to accelerate Phase 1 reconnaissance. It supplements — but does not replace — the reconnaissance agents' own source analysis. Agents should verify all claims against current source.

## Product Overview

Spokes (Manager Poly-Robotics) is a business management platform for robotics/automation companies. It handles invoicing, quotes, project management, scheduling, and customer relationship management.

## Technology Stack

- **Framework**: ASP.NET Core Blazor Server (stateful SignalR circuits)
- **UI**: MudBlazor component library
- **Data Persistence**: Custom file-based JSON persistence via `JsonRepository<T>` — not Entity Framework or a relational database
- **Real-time**: SignalR hubs for live updates
- **Voice/Video**: LiveKit integration with separate JWT tokens
- **Hosting**: Docker containers, self-hosted by customers

## Authentication Architecture

Spokes uses a dual-path authentication model via a custom `PolicyScheme`:

### Desktop/Browser Path
- **Cookie-based**: `Spokes_Session_v3` cookie backed by `DeviceSessionTicketStore`
- **Refresh**: `Spokes_Refresh` cookie for session renewal
- **Protocol**: OpenID Connect (OIDC) with authorization code flow

### Mobile/Capacitor Path
- **Bearer token**: `Authorization: Bearer` headers
- **Validation**: `DeviceTokenAuthHandler` validates tokens
- **Protocol**: Native PKCE flow

### Token Validation
- All refresh tokens MUST be validated through `SessionService.ValidateToken(rawToken, expectedDeviceId)`
- Token validation binds tokens to specific device IDs
- Any token parsing that bypasses `SessionService` is a potential finding

## Data Persistence Model

- **`JsonRepository<T>`**: File-based JSON storage with atomic file operations
- **Critical rule**: Modifying an object's properties in memory without calling `_repository.Save(entity)` results in silent data loss on restart
- **`SequenceService`**: Generates human-readable tracking numbers (e.g., `INV-2401-001`)
- **No ORM**: No Entity Framework, no SQL — all data is serialized JSON files on disk

### Trust-Relevant Persistence Concerns
- File path construction from user-supplied identifiers (potential path traversal)
- Concurrent file access (TOCTOU between read and write)
- No database-level row isolation — tenant isolation depends entirely on application logic
- Backup/restore operations may cross tenant boundaries

## Entry Surfaces

### HTTP API Endpoints
- `/spokesapi/...` — REST API endpoints (should require `[Authorize]`)
- SignalR hub connections for real-time UI updates
- File upload endpoints for attachments and documents

### Blazor Server Components
- Server-side rendered components with stateful circuits
- Component parameters and cascading values carry authentication state
- `@attribute [Authorize]` on pages/components

### LiveKit Integration
- Separate JWT generation using `LiveKitApiKey` and `LiveKitApiSecret`
- Voice/video room tokens with room-level access control

## Key Trust Boundaries

1. **Authentication boundary**: PolicyScheme → cookie or bearer path → identity claims
2. **Authorization boundary**: `[Authorize]` attributes, role checks, owner checks on data access
3. **Tenant isolation boundary**: Application-level filtering in `JsonRepository<T>` queries — no database enforcement
4. **File system boundary**: JSON data files on disk, file upload storage, backup archives
5. **LiveKit boundary**: Separate JWT scope from main session — room access tokens

## Deployment Model

- Self-hosted Docker containers by customers
- Reverse proxy (typically Nginx or Caddy) in front of Kestrel
- HTTPS termination at the proxy level
- License validation via `.spokes-license` file
