using System.Globalization;
using FileBridge.Core;

namespace FileBridge.Desktop;

internal static class TransferDisplay
{
    public static string StageText(TransferProgress progress, Localization language) => progress.Stage switch
    {
        TransferStage.Pending => language.Text("Core.Pending"),
        TransferStage.CalculatingHash => language.Text("Core.CalculatingHash"),
        TransferStage.VerifyingResume => language.Text("Core.ResumeCheck"),
        TransferStage.WaitingForData => language.Text("Core.WaitingForData"),
        TransferStage.Receiving => language.Text("Core.Receiving"),
        TransferStage.VerifyingFile => language.Text("Core.Verifying"),
        TransferStage.ReceiveComplete => language.Text("Core.ReceiveComplete"),
        TransferStage.ResendingCorrupt => language.Text("Core.ResendingCorrupt"),
        TransferStage.Sending => language.Text("Core.Sending"),
        TransferStage.SendComplete => language.Text("Core.SendVerified"),
        TransferStage.AlreadyComplete => language.Text("Core.AlreadyComplete"),
        TransferStage.Retrying => language.Text("CorePrefix.Retry") + $"{progress.RetryAttempt}/{progress.RetryLimit}",
        TransferStage.ReceiveError => language.Text("CorePrefix.ReceiveError") + language.CoreText(progress.Error ?? ""),
        _ => throw new ArgumentOutOfRangeException(nameof(progress), progress.Stage, "Unknown transfer stage.")
    };

    public static string Bytes(long value, CultureInfo culture) => value >= 1L << 30
        ? (value / (double)(1L << 30)).ToString("0.##", culture) + " GiB"
        : value >= 1L << 20 ? (value / (double)(1L << 20)).ToString("0.##", culture) + " MiB"
        : value.ToString("N0", culture) + " B";

    // A stopped sender cannot notify the receiver; expire stale samples instead of showing a frozen speed/ETA.
    public static double ReceiveSpeed(TransferProgress progress, bool active, long idleMilliseconds) =>
        active && idleMilliseconds < 5000 && double.IsFinite(progress.BytesPerSecond) ? Math.Max(0, progress.BytesPerSecond) : 0;

    public static string ParallelMode(int streams, Localization language) => streams <= 0 ? language.Text("ParallelNegotiating")
        : streams == 1 ? language.Text("ParallelDisabled") : language.Text("ParallelEnabled", streams);

    public static string ReceiveEstimate(TransferProgress progress, double speed, Localization language)
    {
        if (progress.Stage == TransferStage.VerifyingFile) return language.Text("TimeVerifying");
        if (progress.Stage == TransferStage.ReceiveComplete) return language.Text("TimeComplete");
        if (progress.Stage != TransferStage.Receiving || speed <= 0 || !double.IsFinite(speed)) return language.Text("TimeWaiting");
        if (progress.Completed >= progress.Total) return language.Text("TimeVerifying");
        var seconds = Math.Ceiling((progress.Total - progress.Completed) / speed);
        var duration = seconds >= 3600 ? language.Text("DurationHours", Math.Floor(seconds / 3600), Math.Floor(seconds % 3600 / 60))
            : seconds >= 60 ? language.Text("DurationMinutes", Math.Floor(seconds / 60), seconds % 60)
            : language.Text("DurationSeconds", Math.Max(1, seconds));
        return language.Text("TimeRemaining", duration);
    }
}
