using Microsoft.Extensions.Logging;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Holidays;
using Urlaubstool.Infrastructure.Paths;

namespace Urlaubstool.Infrastructure.Services;

/// <summary>
/// Manages synchronization of school and public holidays from online sources.
/// Supports periodic updates, background syncing of all states, and user-initiated refreshes.
/// </summary>
public sealed class HolidaysSyncService
{
    private readonly OnlineHolidayService _onlineHolidayService;
    private readonly OnlinePublicHolidayService _onlinePublicHolidayService;
    private readonly PathService _pathService;
    private readonly ILogger<HolidaysSyncService> _logger;
    private readonly ISchoolHolidayProvider _schoolHolidayProvider;
    private readonly IPublicHolidayProvider _publicHolidayProvider;

    private Timer? _periodicSyncTimer;
    private DateTime _lastSyncUtc = DateTime.MinValue;
    private readonly TimeSpan _syncInterval = TimeSpan.FromHours(24);
    private readonly object _lock = new();

    public event EventHandler<SyncCompletedEventArgs>? SyncCompleted;

    public HolidaysSyncService(
        PathService pathService,
        ISchoolHolidayProvider schoolHolidayProvider,
        IPublicHolidayProvider publicHolidayProvider,
        ILogger<HolidaysSyncService> logger)
    {
        _pathService = pathService;
        _schoolHolidayProvider = schoolHolidayProvider;
        _publicHolidayProvider = publicHolidayProvider;
        _logger = logger;
        _onlineHolidayService = new OnlineHolidayService();
        _onlinePublicHolidayService = new OnlinePublicHolidayService(null);  // Logger type mismatch; use null
    }

    /// <summary>
    /// Starts periodic background sync. Safe to call multiple times.
    /// </summary>
    public void StartPeriodicSync()
    {
        lock (_lock)
        {
            if (_periodicSyncTimer != null)
            {
                return; // Already running
            }

            _logger.LogInformation("Starting periodic holiday sync (interval: {Interval})", _syncInterval);
            _periodicSyncTimer = new Timer(_ => SyncIfNeeded(), null, TimeSpan.Zero, _syncInterval);
        }
    }

    /// <summary>
    /// Stops periodic background sync.
    /// </summary>
    public void StopPeriodicSync()
    {
        lock (_lock)
        {
            if (_periodicSyncTimer != null)
            {
                _periodicSyncTimer.Dispose();
                _periodicSyncTimer = null;
                _logger.LogInformation("Stopped periodic holiday sync");
            }
        }
    }

    /// <summary>
    /// Syncs holidays only if last sync was older than the interval.
    /// </summary>
    private void SyncIfNeeded()
    {
        if (DateTime.UtcNow - _lastSyncUtc < _syncInterval)
        {
            return;
        }

        _ = SyncAllAsync();
    }

    /// <summary>
    /// Manually trigger a full sync for current and next year, all states.
    /// Returns true if at least one source was updated successfully.
    /// </summary>
    public async Task<bool> SyncAllAsync()
    {
        try
        {
            _logger.LogInformation("Starting full holiday sync");
            var year = DateTime.Today.Year;
            var schoolHolidaysCachePath = GetSchoolHolidaysCachePath();
            var publicHolidaysCachePath = GetPublicHolidaysCachePath();

            var schoolSuccessful = false;
            var publicSuccessful = false;

            // Sync school holidays for all states
            schoolSuccessful = await SyncSchoolHolidaysAllStatesAsync(schoolHolidaysCachePath, year);

            // Sync public holidays for all years
            publicSuccessful = await SyncPublicHolidaysAsync(publicHolidaysCachePath, year);

            _lastSyncUtc = DateTime.UtcNow;

            var result = new SyncCompletedEventArgs
            {
                Success = schoolSuccessful || publicSuccessful,
                SchoolHolidaysUpdated = schoolSuccessful,
                PublicHolidaysUpdated = publicSuccessful,
                CompletedAtUtc = _lastSyncUtc
            };

            SyncCompleted?.Invoke(this, result);
            _logger.LogInformation("Full holiday sync completed: {Result}", result);

            return result.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during full holiday sync");
            SyncCompleted?.Invoke(this, new SyncCompletedEventArgs
            {
                Success = false,
                CompletedAtUtc = DateTime.UtcNow,
                ErrorMessage = ex.Message
            });
            return false;
        }
    }

    /// <summary>
    /// Syncs school holidays for a specific state and year.
    /// </summary>
    public async Task<bool> SyncSchoolHolidaysAsync(string state, int year)
    {
        try
        {
            var cachePath = GetSchoolHolidaysCachePath();
            var dir = Path.GetDirectoryName(cachePath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var updated = await _onlineHolidayService.FetchAndCacheAsync(state, year, cachePath);
            
            if (updated)
            {
                ReloadSchoolHolidayProvider(cachePath);
                _logger.LogInformation("School holidays updated for {State} {Year}", state, year);
            }

            return updated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing school holidays for {State} {Year}", state, year);
            return false;
        }
    }

    /// <summary>
    /// Syncs school holidays for all states (current and next year).
    /// </summary>
    private async Task<bool> SyncSchoolHolidaysAllStatesAsync(string cachePath, int year)
    {
        var dir = Path.GetDirectoryName(cachePath);
        if (dir != null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tasks = new List<Task<bool>>();
        
        // Sync all states for current and next year
        foreach (var state in Bundeslaender.Codes)
        {
            tasks.Add(_onlineHolidayService.FetchAndCacheAsync(state, year, cachePath));
            tasks.Add(_onlineHolidayService.FetchAndCacheAsync(state, year + 1, cachePath));
        }

        var results = await Task.WhenAll(tasks);
        var anyUpdated = results.Any(r => r);

        if (anyUpdated)
        {
            ReloadSchoolHolidayProvider(cachePath);
            _logger.LogInformation("School holidays updated for all states");
        }

        return anyUpdated;
    }

    /// <summary>
    /// Syncs public holidays for a specific year.
    /// </summary>
    public async Task<bool> SyncPublicHolidaysAsync(int year)
    {
        return await SyncPublicHolidaysAsync(GetPublicHolidaysCachePath(), year);
    }

    /// <summary>
    /// Syncs public holidays for current and next year.
    /// </summary>
    private async Task<bool> SyncPublicHolidaysAsync(string cachePath, int year)
    {
        try
        {
            var dir = Path.GetDirectoryName(cachePath);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tasks = new List<Task<bool>>
            {
                _onlinePublicHolidayService.FetchAndCacheAsync(year, cachePath),
                _onlinePublicHolidayService.FetchAndCacheAsync(year + 1, cachePath)
            };

            var results = await Task.WhenAll(tasks);
            var anyUpdated = results.Any(r => r);

            if (anyUpdated)
            {
                ReloadPublicHolidayProvider(cachePath);
                _logger.LogInformation("Public holidays updated for years {Year} and {NextYear}", year, year + 1);
            }

            return anyUpdated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing public holidays");
            return false;
        }
    }

    private void ReloadSchoolHolidayProvider(string cachePath)
    {
        if (_schoolHolidayProvider is SchoolHolidayProvider provider)
        {
            provider.Reload(cachePath);
        }
    }

    private void ReloadPublicHolidayProvider(string cachePath)
    {
        if (_publicHolidayProvider is HybridPublicHolidayProvider provider)
        {
            provider.Reload(cachePath);
        }
    }

    private string GetSchoolHolidaysCachePath()
    {
        return Path.Combine(_pathService.GetAppDataDirectory(), "school_holidays_cache.json");
    }

    private string GetPublicHolidaysCachePath()
    {
        return Path.Combine(_pathService.GetAppDataDirectory(), "public_holidays_cache.json");
    }

    /// <summary>
    /// Gets the timestamp of the last successful sync.
    /// </summary>
    public DateTime GetLastSyncTimeUtc()
    {
        return _lastSyncUtc;
    }
}

/// <summary>
/// Event arguments for sync completion.
/// </summary>
public sealed class SyncCompletedEventArgs : EventArgs
{
    public bool Success { get; set; }
    public bool SchoolHolidaysUpdated { get; set; }
    public bool PublicHolidaysUpdated { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }

    public override string ToString()
    {
        if (!Success)
        {
            return $"Sync failed: {ErrorMessage}";
        }

        var updates = new List<string>();
        if (SchoolHolidaysUpdated) updates.Add("school holidays");
        if (PublicHolidaysUpdated) updates.Add("public holidays");

        return $"Sync successful at {CompletedAtUtc:u}. Updated: {string.Join(", ", updates)}";
    }
}
