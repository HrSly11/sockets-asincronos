using ChatCliente.Network;

namespace ChatCliente;

public sealed class UiFileConflictResolver(Func<Form?> ownerProvider) : IFileConflictResolver
{
    private readonly Func<Form?> ownerProvider =
        ownerProvider ?? throw new ArgumentNullException(nameof(ownerProvider));

    internal Action<Form, Form>? BeforeDialogShow { get; set; }

    public ValueTask<FileConflictDecision> ResolveAsync(
        FileConflictContext conflict,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<FileConflictDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = ownerProvider();
        if (owner is null || owner.IsDisposed || !owner.IsHandleCreated)
        {
            throw new InvalidOperationException(
                "A visible application window is required to resolve the file conflict.");
        }

        var stateLock = new object();
        FileConflictDialog? visibleDialog = null;
        var suppressDialogCompletion = false;

        void CloseVisibleDialog()
        {
            FileConflictDialog? dialog;
            lock (stateLock)
            {
                dialog = visibleDialog;
            }

            if (dialog is null || dialog.IsDisposed)
            {
                return;
            }

            void Close()
            {
                if (!dialog.IsDisposed)
                {
                    dialog.DialogResult = DialogResult.Cancel;
                    dialog.Close();
                }
            }

            try
            {
                if (dialog.InvokeRequired)
                {
                    dialog.BeginInvoke(Close);
                }
                else
                {
                    Close();
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or ObjectDisposedException)
            {
            }
        }

        var cancellationRegistration = cancellationToken.Register(
            () =>
            {
                if (completion.TrySetCanceled(cancellationToken))
                {
                    CloseVisibleDialog();
                }
            });

        void ShowDialog()
        {
            if (completion.Task.IsCompleted)
            {
                return;
            }

            FileConflictDialog? dialog = null;
            try
            {
                dialog = new FileConflictDialog(conflict);
                lock (stateLock)
                {
                    if (completion.Task.IsCompleted)
                    {
                        dialog.Dispose();
                        return;
                    }

                    visibleDialog = dialog;
                }

                dialog.FormClosed += (_, _) =>
                {
                    lock (stateLock)
                    {
                        if (suppressDialogCompletion)
                        {
                            return;
                        }

                        if (ReferenceEquals(visibleDialog, dialog))
                        {
                            visibleDialog = null;
                        }
                    }

                    if (dialog.Decision.HasValue)
                    {
                        completion.TrySetResult(dialog.Decision.Value);
                    }
                    else
                    {
                        completion.TrySetCanceled();
                    }

                    dialog.Dispose();
                };
                BeforeDialogShow?.Invoke(owner, dialog);
                if (owner.IsDisposed || !owner.IsHandleCreated)
                {
                    throw new InvalidOperationException(
                        "The conflict dialog owner is no longer available.");
                }

                dialog.Show(owner);
                dialog.BringToFront();
                dialog.Activate();
            }
            catch (Exception exception)
            {
                lock (stateLock)
                {
                    suppressDialogCompletion = true;
                    if (ReferenceEquals(visibleDialog, dialog))
                    {
                        visibleDialog = null;
                    }
                }

                dialog?.Dispose();
                completion.TrySetException(exception);
            }
        }

        try
        {
            if (owner.InvokeRequired)
            {
                owner.BeginInvoke(ShowDialog);
            }
            else
            {
                ShowDialog();
            }
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return new ValueTask<FileConflictDecision>(
            AwaitResolutionAsync(completion.Task, cancellationRegistration));
    }

    private static async Task<FileConflictDecision> AwaitResolutionAsync(
        Task<FileConflictDecision> resolution,
        CancellationTokenRegistration cancellationRegistration)
    {
        try
        {
            return await resolution.ConfigureAwait(false);
        }
        finally
        {
            cancellationRegistration.Dispose();
        }
    }
}
