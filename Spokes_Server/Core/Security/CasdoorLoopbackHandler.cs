using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Spokes_Server.Core.Security;

public class CasdoorLoopbackHandler : DelegatingHandler
{
    private readonly string _authority;

    public CasdoorLoopbackHandler(string authority)
    {
        _authority = authority.TrimEnd('/');
        InnerHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Unconditionally rewrite all backchannel requests to the local Casdoor instance.
        // This handler is only used when ProviderType == BuiltInCasdoor, so every request
        // through it should reach Casdoor at 127.0.0.1:8000 regardless of the URL scheme
        // or host advertised in the OIDC discovery document.
        if (request.RequestUri != null)
        {
            var builder = new UriBuilder(request.RequestUri)
            {
                Scheme = "http",
                Host = "127.0.0.1",
                Port = 8000
            };
            request.RequestUri = builder.Uri;
        }
        return base.SendAsync(request, cancellationToken);
    }
}

public class AbsoluteUriDocumentRetriever : Microsoft.IdentityModel.Protocols.IDocumentRetriever
{
    private readonly Microsoft.IdentityModel.Protocols.HttpDocumentRetriever _inner;
    private readonly string _authority;

    public AbsoluteUriDocumentRetriever(System.Net.Http.HttpClient backchannel, string authority)
    {
        _inner = new Microsoft.IdentityModel.Protocols.HttpDocumentRetriever(backchannel);
        _authority = authority.TrimEnd('/');
    }

    public bool RequireHttps
    {
        get => _inner.RequireHttps;
        set => _inner.RequireHttps = value;
    }

    public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
    {
        if (address.StartsWith("/"))
        {
            address = _authority + address;
        }
        else if (address.StartsWith("http://127.0.0.1:8000"))
        {
            address = _authority + address.Substring("http://127.0.0.1:8000".Length);
        }
        else if (address.StartsWith("http://localhost:8000"))
        {
            address = _authority + address.Substring("http://localhost:8000".Length);
        }
        else if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var slashIdx = address.IndexOf('/');
            if (slashIdx >= 0)
            {
                address = _authority + address.Substring(slashIdx);
            }
        }

        return _inner.GetDocumentAsync(address, cancel);
    }
}
