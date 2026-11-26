using System.Globalization;
using OverTranslate.Services;

namespace OverTranslate.Views.Shell;

/// <summary>What the title bar's update card is saying on its second line.</summary>
public enum UpdateCardStatus
{
    /// <summary>Nothing asked for: the hourly check and when it last got an answer.</summary>
    Idle,
    Checking,
    UpToDate,
    Failed,

    /// <summary>A newer release is known; the button becomes 立即更新.</summary>
    Found,
}

/// <summary>What the card's button does in the current state.</summary>
public enum UpdateCardAction
{
    Check,
    Retry,
    UpdateNow,
}

/// <summary>
/// The title bar's update card, minus the drawing: which state its second line is in, and what
/// its button does. Kept apart from the window so the transitions can be tested.
/// </summary>
/// <remarks>
/// <para>A found release outranks everything else: once <see cref="UpdateNotifier.Available"/>
/// points at one, the card says so whatever the last check came back with, because the one thing
/// to do then is install it.</para>
///
/// <para>The outcome of a check the user asked for lasts as long as the card stays open. Opening it
/// again starts from the idle line, so a "已是最新版本" from an hour ago is not presented as news;
/// the time on the idle line is what carries it.</para>
/// </remarks>
public sealed class UpdateCardState
{
    public UpdateCardStatus Status { get; private set; } = UpdateCardStatus.Idle;

    public UpdateCardAction Action => Status switch
    {
        UpdateCardStatus.Found  => UpdateCardAction.UpdateNow,
        UpdateCardStatus.Failed => UpdateCardAction.Retry,
        _                       => UpdateCardAction.Check,
    };

    /// <summary>False only while a check is running; the button stays where it is, greyed.</summary>
    public bool ActionEnabled => Status != UpdateCardStatus.Checking;

    /// <summary>The card is being opened.</summary>
    public void Open(bool updateAvailable)
    {
        // A check the user started keeps running across a close and reopen, and the card has to
        // go on saying so or its button would offer a second one.
        if (Status == UpdateCardStatus.Checking) return;
        Status = updateAvailable ? UpdateCardStatus.Found : UpdateCardStatus.Idle;
    }

    /// <summary>
    /// The button was pressed to check. Returns false when there is nothing to start — a check is
    /// already running, or a release is already known.
    /// </summary>
    public bool BeginCheck()
    {
        if (Status is UpdateCardStatus.Checking or UpdateCardStatus.Found) return false;
        Status = UpdateCardStatus.Checking;
        return true;
    }

    /// <summary>The check started by <see cref="BeginCheck"/> came back.</summary>
    /// <param name="updateAvailable">Whether a release is known now, from this check or another.</param>
    public void CompleteCheck(UpdateCheckStatus result, bool updateAvailable)
    {
        if (updateAvailable)
        {
            Status = UpdateCardStatus.Found;
            return;
        }

        Status = result switch
        {
            UpdateCheckStatus.UpToDate => UpdateCardStatus.UpToDate,
            // Only from a build Velopack does not manage (the IDE, an unpackaged folder), where no
            // check can succeed; saying it failed is truer than saying it is up to date.
            _ => UpdateCardStatus.Failed,
        };
    }

    /// <summary>
    /// The periodic check found a release while the card was open. A check the user started is
    /// left to finish and report it through <see cref="CompleteCheck"/>.
    /// </summary>
    public void ReleaseFound()
    {
        if (Status != UpdateCardStatus.Checking) Status = UpdateCardStatus.Found;
    }

    /// <summary>
    /// The time on the idle line: hours and minutes when it was today, with the date in front
    /// otherwise, so a window left open overnight does not pass yesterday's check off as today's.
    /// </summary>
    /// <param name="datePattern">The language's own month/day pattern, from S.UpdateCard.DatePattern.</param>
    public static string FormatLastChecked(DateTime when, DateTime now, string datePattern)
    {
        var time = when.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (when.Date == now.Date) return time;
        return when.ToString(datePattern, CultureInfo.InvariantCulture) + " " + time;
    }
}
