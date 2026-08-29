using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shellvis.Core.Agent;

namespace Shellvis.Shell.Agent;

/// <summary>
/// Asks the user for permission with a dialog over the pill.
///
/// The gate is called from the agent's background thread, so every step has to hop to
/// the UI thread and then block the caller until an answer comes back. That blocking is
/// intentional: the tool call must not proceed until the human has decided, and the
/// agent loop is explicitly built to await this.
///
/// Timeout resolves to Deny, never to Allow. An unattended machine must not accumulate
/// approvals just because nobody was watching.
/// </summary>
internal sealed partial class PillApprovalGate(DispatcherQueue dispatcher, Func<XamlRoot?> xamlRoot)
    : IApprovalGate, Shellvis.Core.Hooks.IHookConsent
{
    /// <summary>
    /// How long a prompt waits before refusing on its own. Generous, because the
    /// timeout exists to stop an unattended machine accumulating approvals, not to
    /// hurry a human who is reading the command.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Serialises dialogs. WinUI permits exactly one ContentDialog at a time and a
    /// second ShowAsync throws; without this, a model that retries after a refusal
    /// triggers a cascade where every follow-up attempt is auto-denied by the
    /// exception handler rather than actually being asked about.
    /// </summary>
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public async Task<ApprovalDecision> RequestAsync(
        ApprovalRequest request, CancellationToken cancellationToken)
    {
        // Queue behind any dialog already on screen. The wait is deliberately not
        // subject to the display timeout: a request that waited its turn deserves a
        // full window of its own once it gets there.
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await AskAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>
    /// First-use consent for a hook.
    ///
    /// Implemented on this class rather than in a second gate, and that is the point: a
    /// hook prompt and an approval prompt are both ContentDialogs, WinUI allows exactly
    /// one at a time, and two independent owners would race the way the approval cascade
    /// did before the semaphore existed. One lock, one queue.
    /// </summary>
    public async Task<bool> AllowAsync(
        Shellvis.Core.Hooks.HookDefinition hook, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            if (!dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    completion.TrySetResult(await ShowHookAsync(hook).ConfigureAwait(true));
                }
                catch (Exception)
                {
                    completion.TrySetResult(false);
                }
            }))
            {
                return false;
            }

            using var timeout = new CancellationTokenSource(Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeout.Token);

            using CancellationTokenRegistration registration = linked.Token.Register(
                () => completion.TrySetResult(false));

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<bool> ShowHookAsync(Shellvis.Core.Hooks.HookDefinition hook)
    {
        XamlRoot? root = xamlRoot();

        if (root is null)
            return false;

        var body = new StackPanel { Spacing = 8 };

        body.Children.Add(new TextBlock
        {
            Text = hook.Command,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });

        body.Children.Add(new TextBlock
        {
            // Says what the grant actually is. A pre_tool_call hook runs on every
            // matching tool call and can veto it, which is a much larger permission
            // than the word "hook" suggests.
            Text = $"Configured for {Shellvis.Core.Hooks.HookCatalog.NameOf(hook.Event)}"
                + (hook.Matcher is null ? " on every tool." : $" on tools matching {hook.Matcher}.")
                + " It runs with your rights and may block or rewrite what the agent does.",
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Run this hook?",
            Content = body,
            PrimaryButtonText = "Allow",
            CloseButtonText = "Never",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<ApprovalDecision> AskAsync(
        ApprovalRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ApprovalDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                completion.TrySetResult(await ShowAsync(request).ConfigureAwait(true));
            }
            catch (Exception)
            {
                // A dialog that cannot be shown must not be read as consent.
                completion.TrySetResult(ApprovalDecision.Deny);
            }
        }))
        {
            // The UI thread is gone; nothing can be approved any more.
            return ApprovalDecision.Deny;
        }

        using var timeout = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);

        using CancellationTokenRegistration registration = linked.Token.Register(
            () => completion.TrySetResult(ApprovalDecision.Deny));

        return await completion.Task.ConfigureAwait(false);
    }

    private async Task<ApprovalDecision> ShowAsync(ApprovalRequest request)
    {
        XamlRoot? root = xamlRoot();
        if (root is null)
            return ApprovalDecision.Deny;

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Shellvis needs your say-so",
            Content = BuildBody(request),
            PrimaryButtonText = "Once",
            SecondaryButtonText = "Session",
            CloseButtonText = "Deny",
            // Deny is the default so that dismissing the dialog with Escape, or
            // hitting Enter without reading, refuses rather than permits.
            DefaultButton = ContentDialogButton.Close,
        };

        // An AlwaysAsk tool must not offer a way to stop being asked. That is the
        // entire difference between it and a merely mutating one.
        if (request.Tool.SideEffect == Core.Tools.SideEffect.AlwaysAsk)
            dialog.SecondaryButtonText = string.Empty;

        ContentDialogResult result = await dialog.ShowAsync();

        return result switch
        {
            ContentDialogResult.Primary => ApprovalDecision.Once,
            ContentDialogResult.Secondary => ApprovalDecision.Session,
            _ => ApprovalDecision.Deny,
        };
    }

    private static StackPanel BuildBody(ApprovalRequest request)
    {
        var panel = new StackPanel { Spacing = 8 };

        panel.Children.Add(new TextBlock
        {
            Text = request.Preview,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });

        panel.Children.Add(new TextBlock
        {
            Text = request.Reason,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });

        // Arguments go in an expander: the preview answers "what is this" at a glance,
        // and the detail is there for the cases where the preview is not enough.
        panel.Children.Add(new Expander
        {
            Header = "Arguments",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new TextBlock
            {
                Text = request.Arguments,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
        });

        return panel;
    }
}
