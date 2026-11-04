using System;
using Microsoft.AspNetCore.Builder;

namespace Spokes_Server.Core.Security;

/// <summary>
/// Marker metadata indicating that an endpoint remains accessible before initial server setup completes.
/// </summary>
public interface IAllowDuringSetupMetadata
{
}

/// <summary>
/// Specifies that the target controller, action, Razor component, or minimal API handler
/// is accessible while <c>SystemConfig.IsSetupComplete</c> is <c>false</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Delegate, AllowMultiple = false, Inherited = true)]
public sealed class AllowDuringSetupAttribute : Attribute, IAllowDuringSetupMetadata
{
}

public static class AllowDuringSetupEndpointConventionBuilderExtensions
{
    private static readonly AllowDuringSetupAttribute MetadataInstance = new();

    /// <summary>
    /// Marks all endpoints produced by <paramref name="builder"/> as accessible while initial server setup is incomplete.
    /// </summary>
    public static TBuilder AllowDuringSetup<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpointBuilder => endpointBuilder.Metadata.Add(MetadataInstance));
        return builder;
    }
}
