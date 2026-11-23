namespace Spokes_Server.Components.Pages.Chat;

/// <summary>
/// Server-side reference wrapper for large chat text passed between Blazor Server components.
/// Prevents Blazor's RenderBatchWriter from serializing raw string component parameters into
/// RenderBatch.Strings over SignalR when the text is only needed on the server (e.g. full
/// un-truncated code blocks inspected by ChatMarkdownRenderer).
/// </summary>
public sealed record ChatTextRef(string Value);
