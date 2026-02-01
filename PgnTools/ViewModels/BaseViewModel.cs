using System.Collections.Generic;
using System.Diagnostics;

namespace PgnTools.ViewModels;

public partial class BaseViewModel() : ObservableObject
{
    private Stopwatch? _statusStopwatch;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;

    [ObservableProperty]
    private string _statusDetail = string.Empty;

    protected void StartProgressTimer()
    {
        _statusStopwatch = Stopwatch.StartNew();
    }

    protected void StopProgressTimer()
    {
        _statusStopwatch?.Stop();
        _statusStopwatch = null;
    }

    protected string BuildProgressDetail(
        double? percent = null,
        long? current = null,
        long? total = null,
        string unit = "items")
    {
        var parts = new List<string>();

        if (percent.HasValue)
        {
            parts.Add($"{percent:0}%");
        }

        if (current.HasValue && total.HasValue && total.Value > 0)
        {
            parts.Add($"{current:N0}/{total:N0} {unit}");
        }
        else if (current.HasValue)
        {
            parts.Add($"{current:N0} {unit}");
        }

        if (_statusStopwatch != null)
        {
            parts.Add($"Elapsed {_statusStopwatch.Elapsed:hh\\:mm\\:ss}");

            var elapsed = _statusStopwatch.Elapsed;
            if (elapsed.TotalSeconds >= 1)
            {
                if (current.HasValue)
                {
                    var rate = current.Value / elapsed.TotalSeconds;
                    if (rate > 0)
                    {
                        parts.Add($"{rate:0.0} {unit}/s");

                        if (total.HasValue && total.Value > current.Value)
                        {
                            var remainingItems = total.Value - current.Value;
                            var remainingSeconds = remainingItems / rate;
                            parts.Add($"ETA {TimeSpan.FromSeconds(remainingSeconds):hh\\:mm\\:ss}");
                        }
                    }
                }
                else if (percent.HasValue && percent.Value is > 0 and < 100)
                {
                    var totalSeconds = elapsed.TotalSeconds * 100 / percent.Value;
                    var remainingSeconds = Math.Max(0, totalSeconds - elapsed.TotalSeconds);
                    parts.Add($"ETA {TimeSpan.FromSeconds(remainingSeconds):hh\\:mm\\:ss}");
                }
            }
        }

        return string.Join(" • ", parts);
    }
}
