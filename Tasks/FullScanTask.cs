using Jellyfin.Plugin.SpecialToMovie.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SpecialToMovie.Tasks;

public class FullScanTask : IScheduledTask
{
    private readonly SpecialDetectionService _detectionService;
    private readonly ILogger<FullScanTask> _logger;

    public FullScanTask(SpecialDetectionService detectionService, ILogger<FullScanTask> logger)
    {
        _detectionService = detectionService;
        _logger = logger;
    }

    public string Name => "Full Scan";

    public string Key => "SpecialToMovieFullScan";

    public string Description => "Scans all Season 0 episodes across configured TV libraries and looks up each against TMDB/TVDB. Catches episodes added before the plugin was installed, episodes missed during downtime, and previously failed lookups that may now succeed. Also promotes DryRun pairs when dry run mode is disabled and re-syncs watch state for all active pairs. In dry run mode, matches are detected and logged but no files are created.";

    public string Category => "SpecialToMovie";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting SpecialToMovie full scan");
        progress.Report(0);

        await _detectionService.RunFullScanAsync(progress, cancellationToken).ConfigureAwait(false);

        progress.Report(100);
        _logger.LogInformation("SpecialToMovie full scan finished");
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.Zero.Ticks // midnight
        };
    }
}
