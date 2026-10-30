using System.Diagnostics;

namespace Dendro.Core.Tabular;

public sealed record TableProgress(double? Percent, string Message, long Rows = 0);

internal sealed class TableProgressReporter(IProgress<TableProgress>? progress)
{
    private long last;
    public void Report(double? percent, string message, long rows = 0, bool force = false)
    {
        if (progress == null) return;
        long now = Stopwatch.GetTimestamp();
        if (!force && last != 0 && Stopwatch.GetElapsedTime(last, now).TotalMilliseconds < 150) return;
        last = now;
        progress.Report(new(percent, message, rows));
    }
}
