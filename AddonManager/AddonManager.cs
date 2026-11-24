using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kxnrl.SteamApi;
using Kxnrl.SteamApi.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using SteamDatabase.ValvePak;
using ISteamApi = Sharp.Shared.Objects.ISteamApi;

namespace AddonManager;

public class AddonManager : IModSharpModule, ISteamListener, IGameListener
{
    int ISteamListener.ListenerVersion  => ISteamListener.ApiVersion;
    int ISteamListener.ListenerPriority => 0;
    int IGameListener. ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener. ListenerPriority => 0;

    string IModSharpModule.DisplayName      => "AddonManager";
    string IModSharpModule.DisplayAuthor    => "Nuko";

    private const int MaxRetries = 5;

    private readonly IModSharp      _modSharp;
    private readonly IClientManager _clientManager;
    private readonly IConVarManager _conVar;
    private          IGlobalVars    _globalVars => _modSharp.GetGlobals();
    private readonly ISteamApi      _steamApi;

    private readonly ulong _addonId;

    private readonly string _vpkPath;
    private readonly string _contentPath;
    private readonly string _lastUpdateTimesFile;
    private readonly string _assetPath;
    private readonly string _configPath;

    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _addonLastUpdateTimes = [];

    private Config _config = new ();
    private bool   _restarted;

    private readonly ConcurrentDictionary<ulong, int>            _retryCounts        = [];
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _pendingUpdateTimes = [];

    private readonly ILogger<AddonManager>   _logger;
    private readonly CancellationTokenSource _token;

    private static readonly JsonSerializerOptions SerializerOptions = new ()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented       = true,
    };

    public AddonManager(
        ISharedSystem  sharedSystem,
        string         dllPath,
        string         sharpPath,
        Version        version,
        IConfiguration configuration,
        bool           hotReload)
    {
        _modSharp      = sharedSystem.GetModSharp();
        _clientManager = sharedSystem.GetClientManager();
        _conVar        = sharedSystem.GetConVarManager();

        if (_modSharp.GetCommandLine("-dual_addon") is not { } addonString || !ulong.TryParse(addonString, out _addonId))
        {
            throw new Exception("Failed to get/parse addon id from startup param \"-dual_addon\"");
        }

        _assetPath = Path.Combine(sharpPath, "assets");

        _configPath = Path.Combine(sharpPath, "configs", "addon_manager.jsonc");

        if (!Directory.Exists(_assetPath))
            Directory.CreateDirectory(_assetPath);

        _contentPath = Path.GetFullPath(Path.Combine(sharpPath,
                                                     "../bin/",
                                                     RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                                                         ? "linuxsteamrt64"
                                                         : "win64",
                                                     "steamapps",
                                                     "workshop",
                                                     "content",
                                                     "730",
                                                     _addonId.ToString()));

        _vpkPath = Path.Combine(_contentPath, _addonId + "_dir.vpk");

        _lastUpdateTimesFile = Path.GetFullPath(Path.Combine(_assetPath, "last_update_time.txt"));

        _steamApi = _modSharp.GetSteamGameServer();
        _logger   = sharedSystem.GetLoggerFactory().CreateLogger<AddonManager>();

        _token = new CancellationTokenSource();
        ReloadConfig();
    }

    public bool Init()
    {
        _modSharp.InstallSteamListener(this);
        _modSharp.InstallGameListener(this);

        _conVar.CreateServerCommand("addon_reload_config", OnCommandReloadConfig);

        return true;
    }

    private void ReloadConfig(bool log = false)
    {
        string content;

        if (!File.Exists(_configPath))
        {
            content = JsonSerializer.Serialize(_config, SerializerOptions);
            File.WriteAllText(_configPath, content);

            if (log)
                _logger.LogWarning("File {path} was not found, creating...", _configPath);

            return;
        }

        try
        {
            content = File.ReadAllText(_configPath);

            if (JsonSerializer.Deserialize<Config>(content, SerializerOptions) is not { } config)
            {
                throw new NullReferenceException("Deserialized json but the result is null???");
            }

            _config = config;

            if (log)
            {
                _logger.LogInformation("Config {path} loaded", _configPath);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when loading config, using default");
            _config = new ();
        }
    }

    private ECommandAction OnCommandReloadConfig(StringCommand arg)
    {
        ReloadConfig(true);

        return ECommandAction.Handled;
    }

    public void Shutdown()
    {
        _modSharp.RemoveSteamListener(this);
        _modSharp.RemoveGameListener(this);

        _conVar.ReleaseCommand("addon_reload_config");

        _token.Cancel();
    }

    public void OnServerActivate()
    {
        if (CheckAndEnforceDualAddon())
        {
            return;
        }

        Task.Run(CheckAddonDetail, _token.Token);

        _modSharp.PushTimer(() => { Task.Run(CheckAddonDetail, _token.Token); },
                            60.0f * _config.Interval,
                            GameTimerFlags.Repeatable | GameTimerFlags.StopOnMapEnd);
    }

    private bool CheckAndEnforceDualAddon()
    {
        if (_restarted)
        {
            return false;
        }

        var    addonName = _modSharp.GetAddonName();
        string mapName;

        try
        {
            mapName = _globalVars.MapName;

            if (string.IsNullOrWhiteSpace(mapName) || mapName == "<empty>")
                mapName = "de_mirage";
        }
        catch (Exception)
        {
            mapName = "de_mirage";
        }

        if (string.IsNullOrWhiteSpace(addonName))
        {
            _logger.LogWarning("AddonName is null or whitespace, restarting");
            TriggerRestart(mapName);

            return true;
        }

        var split       = addonName.Split(',');
        var hasOurAddon = split.Any(s => ulong.TryParse(s, out var result) && result == _addonId);

        if (!hasOurAddon)
        {
            _logger.LogWarning("AddonName does not have our addon id, restarting");
            TriggerRestart(mapName);

            return true;
        }

        return false;
    }

    public void OnDownloadItemResult(ulong sharedFileId, SteamApiResult result)
    {
        if (sharedFileId != _addonId)
            return;

        // Steam can fire hundreds of results for one DownloadItem (659 Success in 14 s on 24 Nov),
        // and the engine downloads the -dual_addon item on its own. Only a download we asked for counts.
        if (!_pendingUpdateTimes.ContainsKey(sharedFileId))
        {
            Interlocked.Increment(ref _ignoredResults);

            return;
        }

        switch (result)
        {
            case SteamApiResult.Success:
                // TryRemove picks exactly one winner among duplicate callbacks.
                if (!_pendingUpdateTimes.TryRemove(sharedFileId, out var newUpdateTime))
                    return;

                _retryCounts.TryRemove(sharedFileId, out _);
                _addonLastUpdateTimes[sharedFileId] = newUpdateTime;

                _logger.LogInformation("Successfully updated addon {id}. New last update time is {time}",
                                       sharedFileId,
                                       newUpdateTime);

                SaveAddonUpdateTimes();
                Task.Run(UpdateAssetsFromPackage, _token.Token);

                // The duplicate results trail in for ~15 s; report them once, after the burst.
                Task.Delay(TimeSpan.FromSeconds(60), _token.Token)
                    .ContinueWith(_ => _logger.LogInformation("Ignored {count} duplicate download results for addon {id}",
                                                              Interlocked.Exchange(ref _ignoredResults, 0),
                                                              sharedFileId),
                                  TaskContinuationOptions.OnlyOnRanToCompletion);

                break;
            case SteamApiResult.Fail:
            case SteamApiResult.NoConnection:
                HandleDownloadRetry(sharedFileId, result);

                break;
            default:
                _logger.LogError("Failed to download addon {id}", sharedFileId);
                _pendingUpdateTimes.TryRemove(sharedFileId, out _);
                _retryCounts.TryRemove(sharedFileId, out _);

                break;
        }
    }

    // Keeps the pending entry so the retried download's Success still passes the gate above.
    private void HandleDownloadRetry(ulong sharedFileId, SteamApiResult reason)
    {
        var currentRetries = _retryCounts.GetValueOrDefault(sharedFileId, 0);

        if (currentRetries < MaxRetries)
        {
            _logger.LogWarning("Failed to download addon {id} due to {reason}. Retrying (Attempt {attempt}/{max}) in 5 seconds...",
                               sharedFileId,
                               reason,
                               currentRetries + 1,
                               MaxRetries);

            _retryCounts[sharedFileId] = currentRetries + 1;
            _steamApi.DownloadItem(sharedFileId, true);
        }
        else
        {
            _logger.LogError("Failed to download addon {id} after {max} attempts. Giving up.", sharedFileId, MaxRetries);
            _retryCounts.TryRemove(sharedFileId, out _);
            _pendingUpdateTimes.TryRemove(sharedFileId, out _);
        }
    }

    private int _extracting;
    private int _ignoredResults;

    private void UpdateAssetsFromPackage()
    {
        if (string.IsNullOrWhiteSpace(_vpkPath))
        {
            return;
        }

        // Single flight: one extraction of the 2 GB vpk at a time, never a queue of them.
        if (Interlocked.Exchange(ref _extracting, 1) == 1)
        {
            _logger.LogWarning("Extraction already running, skipping");

            return;
        }

        try
        {
            HashSet<string> allPackageFilePaths;

            try
            {
                allPackageFilePaths = ExtractVpkContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error extracting VPK: {path}", _vpkPath);

                return;
            }

            CleanupObsoleteFiles(allPackageFilePaths);
            CleanupSourceContent();
        }
        finally
        {
            Volatile.Write(ref _extracting, 0);
        }

        _modSharp.InvokeFrameAction(RestartLevel);
    }

    private HashSet<string> ExtractVpkContent()
    {
        var extractedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var package = new Package();
        package.Read(_vpkPath);

        if (package.Entries is not { } entries)
        {
            _logger.LogError("No entry was found from {path}", _vpkPath);

            return extractedPaths;
        }

        _logger.LogInformation("Extracting vpk with {a} entries", entries.Count);

        foreach (var entry in entries)
        {
            foreach (var value in entry.Value)
            {
                if (string.IsNullOrWhiteSpace(value.DirectoryName))
                {
                    continue;
                }

                var targetDir = Path.Combine(_assetPath, value.DirectoryName);

                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                var fullPath = Path.Combine(_assetPath, value.GetFullPath());
                extractedPaths.Add(Path.GetFullPath(fullPath));

                // Perform extraction
                package.ReadEntry(value, out var bytes, false);
                File.WriteAllBytes(fullPath, bytes);
            }
        }

        return extractedPaths;
    }

    private void CleanupObsoleteFiles(HashSet<string> validPackagePaths)
    {
        if (!_config.RemoveObsoleteFiles)
            return;

        _logger.LogInformation("Finished extracting, scanning for obsolete files to delete");
        validPackagePaths.Add(_lastUpdateTimesFile);

        var allDiskFiles = Directory.GetFiles(_assetPath, "*", SearchOption.AllDirectories);
        var deletedCount = 0;

        foreach (var file in allDiskFiles)
        {
            var normalizedPath = Path.GetFullPath(file);

            // Skip files that exist in the VPK or is the metadata file
            if (validPackagePaths.Contains(normalizedPath))
            {
                continue;
            }

            try
            {
                File.Delete(normalizedPath);
                _logger.LogInformation("Deleted obsolete file: {file}", normalizedPath);
                deletedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete obsolete file: {file}", normalizedPath);
            }
        }

        _logger.LogInformation("Deleted {count} obsolete files.", deletedCount);
    }

    private void CleanupSourceContent()
    {
        if (!_config.RemoveVpkFiles)
        {
            return;
        }

        if (!Directory.Exists(_contentPath))
        {
            return;
        }

        var files    = Directory.GetFiles(_contentPath);
        var idString = _addonId.ToString();

        foreach (var file in files)
        {
            try
            {
                if (Path.GetFileName(file).StartsWith(idString))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete source content file: {file}", file);
            }
        }
    }

    private void RestartLevel()
    {
        var hasActivePlayers = _clientManager.GetGameClients()
                                             .Any(i => i is
                                             {
                                                 IsFakeClient: false,
                                                 IsHltv: false,
                                                 SignOnState: >= SignOnState.Connected,
                                             });

        if (hasActivePlayers)
        {
            return;
        }

        try
        {
            var mapname = _globalVars.MapName;

            if (string.IsNullOrWhiteSpace(mapname))
            {
                mapname = "de_mirage";
            }

            TriggerRestart(mapname);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when trying to restart level, default back to de_mirage");
            TriggerRestart("de_mirage");
        }
    }

    private void TriggerRestart(string mapName)
    {
        _restarted = true;

        _modSharp.ServerCommand(_modSharp.ListWorkshopMaps().Any(Predicate)
                                    ? $"ds_workshop_changelevel {mapName}"
                                    : $"changelevel {mapName}");

        return;

        bool Predicate((ulong PublishFileId, string Name) tuple)
            => string.Equals(tuple.Name, mapName, StringComparison.OrdinalIgnoreCase);
    }

    private async Task CheckAddonDetail()
    {
        try
        {
            LoadAddonUpdateTimes();

            if (string.IsNullOrWhiteSpace(_config.ApiKey))
            {
                _logger.LogWarning("Attempting to get addon detail with invalid steam api key, go to https://steamcommunity.com/dev/apikey to generate one if you haven't.");

                return;
            }

            var service = SteamApiFactory.GetSteamApi<IPublishedFileService>(_config.ApiKey);
            var details = await service.GetDetails([_addonId], true);

            var detail = details.FirstOrDefault(d => d.PublishedFileId == _addonId);

            if (detail == null)
            {
                if (details.Length == 0)
                {
                    _logger.LogWarning("No details returned from Steam API.");
                }
                else
                {
                    _logger.LogWarning("Could not find details for addon {id} in query result.", _addonId);
                }

                return;
            }

            if (detail.IsWaitingForApprove)
            {
                _logger.LogInformation("Addon is waiting for approval, skipping");

                return;
            }

            var localTime = _addonLastUpdateTimes.GetValueOrDefault(detail.PublishedFileId, DateTimeOffset.MinValue);

            var lastUpdateTimeLocal = DateTimeOffset.FromUnixTimeSeconds(detail.TimeUpdated).LocalDateTime;

            _logger.LogInformation("Checking for update for {title}. Remote: {remoteTime}, Local: {localTime}",
                                   detail.Title,
                                   lastUpdateTimeLocal,
                                   localTime);

            // Re-requests on every check while pending: a result can be lost across a map change at boot,
            // and only one of the duplicate results will ever extract.
            if (lastUpdateTimeLocal > localTime)
            {
                _logger.LogInformation("New version of {title} found. Remote: {time}. Starting download...",
                                       detail.Title,
                                       lastUpdateTimeLocal);

                _pendingUpdateTimes[detail.PublishedFileId] = lastUpdateTimeLocal;
                _steamApi.DownloadItem(detail.PublishedFileId, true);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when checking addon details");
        }
    }

    private void LoadAddonUpdateTimes()
    {
        if (!File.Exists(_lastUpdateTimesFile))
            return;

        try
        {
            var content = File.ReadAllText(_lastUpdateTimesFile);
            var times   = JsonSerializer.Deserialize<Dictionary<ulong, DateTimeOffset>>(content, SerializerOptions);

            if (times != null)
            {
                _addonLastUpdateTimes.Clear();

                foreach (var kvp in times)
                {
                    _addonLastUpdateTimes[kvp.Key] = kvp.Value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read addon update times file.");
        }
    }

    private void SaveAddonUpdateTimes()
    {
        try
        {
            var content = JsonSerializer.Serialize(_addonLastUpdateTimes, SerializerOptions);
            File.WriteAllText(_lastUpdateTimesFile, content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write addon update times file.");
        }
    }
}