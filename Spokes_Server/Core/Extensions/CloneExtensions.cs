using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Spokes_Server.Core.Extensions;

public static class CloneExtensions
{
    /// <summary>
    /// Performs a deep clone of an object using System.Text.Json serialization.
    /// This ensures complete detachment from the original reference.
    /// </summary>
    [return: NotNullIfNotNull(nameof(source))]
    public static T? DeepClone<T>(this T? source) where T : class
    {
        if (source == null) return null;
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<T>(json)
            ?? throw new InvalidOperationException($"Failed to deserialize cloned object of type {typeof(T).Name}.");
    }
}
