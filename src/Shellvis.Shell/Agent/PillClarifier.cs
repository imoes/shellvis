using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Shellvis.Core.Agent;

namespace Shellvis.Shell.Agent;

/// <summary>
/// The question dialog, on the same lock as approvals and hook consent.
///
/// <b>Not a second dialog owner.</b> WinUI permits exactly one ContentDialog at a time, and
/// a second ShowAsync throws. This class already learned that once: before the semaphore
/// existed, a model retrying after a refusal produced a cascade in which every follow-up was
/// auto-denied by the exception handler rather than actually being asked. One lock, one
/// queue, and now three kinds of question passing through it.
///
/// <b>Options are content, not buttons.</b> A ContentDialog has three buttons; four options
/// plus a free-text answer do not fit there. So the choices are radio buttons inside the
/// dialog with their descriptions beneath them, "Something else" is always the last one with
/// a text box attached, and only two buttons remain: take it, or dismiss.
/// </summary>
internal sealed partial class PillApprovalGate : IClarifier
{
    /// <summary>
    /// How long a question waits.
    ///
    /// Shorter than the approval timeout on purpose. An unanswered approval blocks something
    /// dangerous and can afford to wait five minutes; an unanswered question blocks the whole
    /// turn while the agent could be getting on with a reasonable assumption. Two minutes is
    /// long enough to read four options and short enough that a forgotten dialog does not
    /// strand the work.
    /// </summary>
    private static readonly TimeSpan QuestionTimeout = TimeSpan.FromMinutes(2);

    public async Task<ClarifyAnswer> AskAsync(
        ClarifyRequest request, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await ShowQuestionAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A dialog that could not be shown is "nobody answered", not "no". The tool turns
            // that into "decide yourself and say what you assumed", which is what should
            // happen when the surface fails.
            return ClarifyAnswer.NotAnswered;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<ClarifyAnswer> ShowQuestionAsync(
        ClarifyRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ClarifyAnswer>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                completion.TrySetResult(await RunQuestionAsync(request).ConfigureAwait(true));
            }
            catch (Exception)
            {
                completion.TrySetResult(ClarifyAnswer.NotAnswered);
            }
        }))
        {
            return ClarifyAnswer.NotAnswered;
        }

        using var deadline = new CancellationTokenSource(QuestionTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, cancellationToken);

        try
        {
            return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ClarifyAnswer.NotAnswered;
        }
    }

    private async Task<ClarifyAnswer> RunQuestionAsync(ClarifyRequest request)
    {
        XamlRoot? root = xamlRoot();

        // No XamlRoot means the window is not up yet. Answering "nobody answered" is right:
        // there is genuinely nobody to ask.
        if (root is null)
            return ClarifyAnswer.NotAnswered;

        var panel = new StackPanel { Spacing = 10 };

        panel.Children.Add(new TextBlock
        {
            Text = request.Question,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        var buttons = new List<RadioButton>();

        foreach (ClarifyOption option in request.Options)
            buttons.Add(OptionButton(option.Label, option.Description));

        // "Something else" is always last and always present. The options are what the agent
        // could think of; the user is not limited to them, and a question that traps them in
        // four wrong answers is worse than not asking.
        RadioButton other = OptionButton("Something else", "Type your own answer.");
        var otherText = new TextBox
        {
            PlaceholderText = "Your answer",
            Margin = new Thickness(28, 0, 0, 0),
            IsEnabled = false,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = false,
        };

        other.Checked += (_, _) =>
        {
            otherText.IsEnabled = true;
            otherText.Focus(FocusState.Programmatic);
        };

        other.Unchecked += (_, _) => otherText.IsEnabled = false;

        foreach (RadioButton button in buttons)
            panel.Children.Add(button);

        panel.Children.Add(other);
        panel.Children.Add(otherText);

        // Pre-selected, because the first option is where a recommendation goes. A dialog
        // that opens with nothing chosen makes the reader do the work twice.
        if (buttons.Count > 0)
            buttons[0].IsChecked = true;

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = request.Header,
            Content = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = "Use this",
            CloseButtonText = "Dismiss",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return ClarifyAnswer.NotAnswered;

        if (other.IsChecked == true)
        {
            string written = otherText.Text.Trim();

            return written.Length > 0
                ? new ClarifyAnswer([], written, true)
                : ClarifyAnswer.NotAnswered;
        }

        var chosen = new List<string>();

        for (int i = 0; i < buttons.Count; i++)
        {
            if (buttons[i].IsChecked == true)
                chosen.Add(request.Options[i].Label);
        }

        return new ClarifyAnswer(chosen, null, true);
    }

    /// <summary>
    /// One choice: the label, and beneath it what the label costs.
    ///
    /// The description is the part that makes the question answerable. Four bare labels ask
    /// the reader to work out the consequences themselves, which is the work they were being
    /// asked about.
    /// </summary>
    private static RadioButton OptionButton(string label, string description)
    {
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
        });

        if (description.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.75,
            });
        }

        return new RadioButton { Content = stack, GroupName = "clarify" };
    }
}
