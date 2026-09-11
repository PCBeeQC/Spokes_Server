# Contributing to Spokes Server

Thank you for your interest in contributing to Spokes! This document provides guidelines for contributing to the project.

## License

Spokes Server is licensed under the [Business Source License 1.1](LICENSE.md). By contributing, you agree that your contributions will be licensed under the same terms.

## Intellectual Property

By submitting a pull request, you agree that all contributed code becomes the property of Logiciel PCBee inc. and will be distributed under the same [Business Source License 1.1](LICENSE.md) as the rest of the project.

## How to Contribute

### Reporting Bugs

- **Security vulnerabilities**: Please see [SECURITY.md](SECURITY.md) — do NOT open a public issue
- **Bugs**: Open a [GitHub Issue](https://github.com/PCBeeQC/Spokes_Server/issues) with:
  - Steps to reproduce
  - Expected vs actual behavior
  - Spokes Server version (`docker inspect` or Admin Settings)
  - Browser and OS

### Submitting Pull Requests

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/my-improvement`)
3. Make your changes
4. Test locally (see Building below)
5. Commit with clear, descriptive messages
6. Push to your fork and open a Pull Request

### Building from Source

```bash
# Prerequisites: .NET 9 SDK
cd Spokes_Server
dotnet restore
dotnet build -c Release

# Or build the Docker image
docker build -t spokes-server .
```

## Code Style

- This is a Blazor Server application using **MudBlazor** components
- Follow existing code patterns and naming conventions
- Use MudBlazor components (`<MudText>`, `<MudButton>`, etc.) for all UI work
- Preserve existing comments and documentation

## Questions?

For general questions about Spokes, visit [spokes.sh](https://www.spokes.sh).
