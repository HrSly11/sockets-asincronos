using System.Windows.Forms;
using ChatCliente;
using ChatCliente.Network;
using Guna.UI2.WinForms;
using Xunit;

namespace Chat.FunctionalTests;

public sealed class UiCancellationAndInputTests
{
    [Fact(Timeout = 10_000)]
    public Task Cancelling_visible_conflict_prompt_closes_it_and_cancels_resolution()
    {
        return RunInStaAsync(
            async () =>
            {
                using var owner = new Form();
                owner.Show();
                using var cancellation = new CancellationTokenSource();
                var resolver = new UiFileConflictResolver(() => owner);
                var resolution = Task.Run(
                    async () => await resolver.ResolveAsync(
                        NewConflict(),
                        cancellation.Token));
                await WaitUntilAsync(() => owner.OwnedForms.Length == 1);

                cancellation.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => resolution);
                await WaitUntilAsync(() => owner.OwnedForms.Length == 0);
            });
    }

    [Fact(Timeout = 10_000)]
    public Task Cancellation_before_ui_dispatch_prevents_late_prompt()
    {
        return RunInStaAsync(
            async () =>
            {
                using var owner = new Form();
                owner.Show();
                using var cancellation = new CancellationTokenSource();
                var resolver = new UiFileConflictResolver(() => owner);
                var resolutionReady = new TaskCompletionSource<Task<FileConflictDecision>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(
                    () =>
                    {
                        var task = resolver.ResolveAsync(
                                NewConflict(),
                                cancellation.Token)
                            .AsTask();
                        resolutionReady.TrySetResult(task);
                    });
                Assert.True(
                    resolutionReady.Task.Wait(TimeSpan.FromSeconds(2)),
                    "The resolver did not queue its UI dispatch.");
                var resolution = resolutionReady.Task.Result;

                cancellation.Cancel();
                for (var iteration = 0; iteration < 20; iteration++)
                {
                    System.Windows.Forms.Application.DoEvents();
                    await Task.Delay(1);
                }

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => resolution);
                Assert.Empty(owner.OwnedForms);
            });
    }

    [Fact(Timeout = 10_000)]
    public Task Closing_owner_without_explicit_choice_cancels_resolution()
    {
        return RunInStaAsync(
            async () =>
            {
                using var owner = new Form();
                owner.Show();
                var resolver = new UiFileConflictResolver(() => owner);
                var resolution = Task.Run(
                    async () => await resolver.ResolveAsync(NewConflict()));
                await WaitUntilAsync(() => owner.OwnedForms.Length == 1);

                owner.Close();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => resolution);
            });
    }

    [Fact(Timeout = 10_000)]
    public Task Owner_disposed_immediately_before_show_disposes_created_dialog()
    {
        return RunInStaAsync(
            async () =>
            {
                using var owner = new Form();
                owner.Show();
                var resolver = new UiFileConflictResolver(() => owner);
                Form? createdDialog = null;
                resolver.BeforeDialogShow = (dialogOwner, dialog) =>
                {
                    createdDialog = dialog;
                    dialogOwner.Dispose();
                };

                var resolution = Task.Run(
                    async () => await resolver.ResolveAsync(NewConflict()));

                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => resolution);
                Assert.NotNull(createdDialog);
                Assert.True(createdDialog.IsDisposed);
                Assert.False(createdDialog.Visible);
            });
    }

    [Fact(Timeout = 10_000)]
    public Task Composer_preserves_leading_trailing_unicode_whitespace_exactly()
    {
        return RunInStaAsync(
            () =>
            {
                using var form = CreateReadyChatForm();
                const string expected = "\u2003 texto 😀 \u00A0";
                MessageRequestedEventArgs? captured = null;
                form.SendMessageRequested += (_, args) => captured = args;
                form.SetMessageDraft(expected);

                FindButton(form, "Enviar mensaje").PerformClick();

                Assert.NotNull(captured);
                Assert.Equal(expected, captured.Message);
                return Task.CompletedTask;
            });
    }

    [Fact(Timeout = 10_000)]
    public Task Composer_accepts_nonempty_unicode_whitespace_only_message()
    {
        return RunInStaAsync(
            () =>
            {
                using var form = CreateReadyChatForm();
                const string expected = "\u2003\u00A0";
                MessageRequestedEventArgs? captured = null;
                form.SendMessageRequested += (_, args) => captured = args;
                form.SetMessageDraft(expected);

                FindButton(form, "Enviar mensaje").PerformClick();

                Assert.NotNull(captured);
                Assert.Equal(expected, captured.Message);
                Assert.Null(form.ComposerErrorText);
                return Task.CompletedTask;
            });
    }

    private static ChatForm CreateReadyChatForm()
    {
        var form = new ChatForm("Me");
        form.Show();
        form.SetConnectionState(true, "Conectado");
        form.SetUsers([new ChatUserView(2, "Peer", true)]);
        Assert.True(form.SelectRecipient(2));
        return form;
    }

    private static FileConflictContext NewConflict()
    {
        return new FileConflictContext(
            "report.txt",
            @"C:\downloads\report.txt",
            2,
            Guid.NewGuid());
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < timeoutAt)
        {
            System.Windows.Forms.Application.DoEvents();
            await Task.Delay(1);
        }

        Assert.True(condition(), "The expected UI state was not reached.");
    }

    private static Guna2Button FindButton(Control root, string accessibleName)
    {
        foreach (Control child in root.Controls)
        {
            if (child is Guna2Button button && button.AccessibleName == accessibleName)
            {
                return button;
            }

            try
            {
                return FindButton(child, accessibleName);
            }
            catch (InvalidOperationException)
            {
            }
        }

        throw new InvalidOperationException($"Button '{accessibleName}' was not found.");
    }

    private static Task RunInStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(
            () =>
            {
                try
                {
                    var task = action();
                    while (!task.IsCompleted)
                    {
                        System.Windows.Forms.Application.DoEvents();
                        Thread.Sleep(1);
                    }

                    task.GetAwaiter().GetResult();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }
}
