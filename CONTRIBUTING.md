# Contributing to Spokes Server

Thank you for your interest in Spokes!

## Project Philosophy: Source-Available for Trust & Auditing

The Spokes Server source code is published openly to provide **complete transparency, enable independent security audits, and empower users to inspect, compile, and self-host their communication infrastructure with confidence**.

To preserve a unified architecture, ensure long-term product stability, and maintain clean intellectual property ownership, **Spokes is developed as a closed-contribution, source-available project. We do not accept external pull requests or code contributions.**

---

## How You Can Help

While we do not accept direct code contributions, community feedback and verification are essential to making Spokes better:

### 1. Reporting Bugs
If you encounter a bug or unexpected behavior:
- Search existing [GitHub Issues](https://github.com/PCBeeQC/Spokes_Server/issues) to avoid duplicates.
- Open a new issue with:
  - Clear steps to reproduce the issue
  - Expected vs. actual behavior
  - Spokes Server version (Visible at the bottom left of the nav menu)
  - Client platform (browser, OS, or mobile device version)
  - Relevant anonymized server logs

### 2. Reporting Security Vulnerabilities
If you discover a security vulnerability or privacy flaw:
- **Do NOT open a public GitHub issue.**
- Please follow our responsible disclosure process outlined in [SECURITY.md](SECURITY.md).
- Security audits, penetration test reports, and responsible disclosures are deeply appreciated and reviewed with high priority.

### 3. Feature Suggestions & Discussions
Have an idea or feedback on workflows?
- Join the discussion on our community forum: [forum.spokes.sh](https://forum.spokes.sh).
- Participate in discussions on [GitHub Issues](https://github.com/PCBeeQC/Spokes_Server/issues).

---

## Building & Auditing from Source

You are fully encouraged to inspect the code, build the binaries yourself, and verify what runs on your hardware:

```bash
# Prerequisites: .NET 9 SDK
cd Spokes_Server
dotnet restore
dotnet build -c Release

# Or build the Docker image locally
docker build -t spokes-server .
```

---

## License

Spokes Server is source-available under the [Business Source License 1.1](LICENSE.md), converting to Apache 2.0 after 4 years. For more details on commercial restrictions and additional grants, please consult [LICENSE.md](LICENSE.md).

For questions or inquiries, visit [spokes.sh](https://www.spokes.sh).
