namespace Spokes_Server.Components.UI;

/// <summary>
/// Result returned by <see cref="IBackDismissHandler.OnBackRequestedAsync"/> when a hardware or
/// gesture Back press targets a dialog.
/// </summary>
public enum BackDismissAction
{
    /// <summary>Proceed with default <see cref="MudBlazor.IDialogService"/> cancellation.</summary>
    DefaultCancel,

    /// <summary>The dialog handled the Back press and initiated its own close.</summary>
    Handled,

    /// <summary>Keep the dialog open and consume the Back press so it cannot leak behind the modal.</summary>
    Block
}

/// <summary>
/// Optional contract for MudDialog components that need custom cleanup or veto logic
/// when dismissed via hardware/gesture Back (e.g., rolling back uncommitted draft uploads or
/// blocking dismissal while an irreversible operation is in progress).
/// </summary>
public interface IBackDismissHandler
{
    Task<BackDismissAction> OnBackRequestedAsync();
}
