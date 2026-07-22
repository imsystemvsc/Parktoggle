using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ParkToggleWpf;

public enum ParkMode
{
    Custom,
    AlwaysOn,
    CoolIdle
}

public enum CoolIdleTier
{
    MaxCool,    // Min Cores: 0%, Idle State Max: 20, Max Perf State: 85%
    Balanced,   // Min Cores: 0%, Idle State Max: 20, Max Perf State: 99% (Default)
    Responsive  // Min Cores: 25%, Idle State Max: 10, Max Perf State: 100%
}

public sealed record PowerPlan(string Guid, string Name, bool IsActive)
{
    public override string ToString() => Name;
}
public sealed record PowerSettingValues(int Ac, int Dc);
public sealed record ModeSnapshot(ParkMode Mode, PowerSettingValues Core, PowerSettingValues Idle);

public sealed class PowerPlanService
{
    private const string LogFileName = "ParkToggle.log";

    private static readonly Guid SubProcessorGuid = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid CoreGuid = new("0cc5b647-c1df-4637-891a-dec35c318583");
    private static readonly Guid IdleGuid = new("9943e905-9a30-4ec1-9b99-44dd3b76f7a2");
    private static readonly Guid MaxPerfStateGuid = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid MinPerfStateGuid = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid PerfIncTimeGuid = new("984cf492-3bed-4488-a8f9-4286c97bf5aa");
    private static readonly Guid CpIncreasePolGuid = new("c7be0679-2817-4d69-9d02-519a537ed0c6");
    private static readonly string VisibilityMarkerPath = ResolveVisibilityMarkerPath();
    private static readonly TimeSpan VisibilityMarkerTtl = TimeSpan.FromDays(7); // periodic refresh in case Windows hides settings again
    private static readonly SemaphoreSlim SettingVisibilityLock = new(1, 1);
    private static bool _settingsVisibilityEnsured;
    private static string? _lastVisibilityError;

    private static readonly Regex PlanLineRegex = new(
        pattern: "^\\s*Power\\s+Scheme\\s+GUID:\\s*([0-9a-fA-F-]+)\\s*\\((.+?)\\)\\s*(\\*)?$",
        options: RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex HexRegex = new(
        pattern: "0x[0-9a-fA-F]+",
        options: RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string _logPath;
    private readonly object _logSync = new();
    private static readonly Encoding ConsoleEncoding = ResolveConsoleEncoding();
    private bool _logInitialized;

    static PowerPlanService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public PowerPlanService()
    {
        var baseDir = AppContext.BaseDirectory;
        var logDirectory = Path.Combine(baseDir, "Logs");

        try
        {
            Directory.CreateDirectory(logDirectory);
        }
        catch
        {
            logDirectory = baseDir;
        }

        _logPath = Path.Combine(logDirectory, LogFileName);
    }

    public string LogPath => _logPath;

    public async Task<IReadOnlyList<PowerPlan>> GetPlansAsync(CancellationToken token = default)
    {
        var result = await RunPowerCfgAsync("/list", token).ConfigureAwait(false);
        EnsureSuccess(result, "powercfg /list");

        var plans = new List<PowerPlan>();
        foreach (var rawLine in EnumerateLines(result.StandardOutput))
        {
            var match = PlanLineRegex.Match(rawLine);
            if (!match.Success)
            {
                continue;
            }

            var guid = match.Groups[1].Value.Trim().Trim('{', '}').ToLowerInvariant();
            var name = match.Groups[2].Value.Trim();
            var isActive = match.Groups[3].Success;
            plans.Add(new PowerPlan(guid, name, isActive));
        }

        if (plans.Count == 0)
        {
            throw new InvalidOperationException("No power plans reported by powercfg.");
        }

        Log( $"Discovered {plans.Count} plans: {string.Join(", ", plans.Select(p => p.Name))}"); 
        return plans;
    }

    public async Task<PowerPlan> GetActivePlanAsync(CancellationToken token = default)
    {
        var result = await RunPowerCfgAsync("/getactivescheme", token).ConfigureAwait(false);
        EnsureSuccess(result, "powercfg /getactivescheme");

        var line = result.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(line))
        {
            throw new InvalidOperationException("powercfg /getactivescheme returned no output.");
        }

        var parts = line.Split(':', 2);
        var tail = parts.Length > 1 ? parts[1].Trim() : parts[0].Trim();
        if (string.IsNullOrWhiteSpace(tail))
        {
            throw new InvalidOperationException($"Unexpected format from powercfg /getactivescheme: \"{line}\".");
        }

        var tokens = tail.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var guid = tokens.Length > 0 ? tokens[0] : "SCHEME_CURRENT";
        guid = guid.Trim().Trim('{', '}').ToLowerInvariant();

        var name = tokens.Length > 1 ? tokens[1].Trim() : "Unknown";
        if (name.StartsWith("(") && name.EndsWith(")"))
        {
            name = name.Trim('(', ')').Trim();
        }

        return new PowerPlan(guid, name, true);
    }

    public async Task<ModeSnapshot> GetModeSnapshotAsync(string planGuid, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(planGuid))
        {
            throw new ArgumentException("Plan GUID cannot be empty.", nameof(planGuid));
        }

        await EnsureCpuParkingSettingsVisibleAsync(token).ConfigureAwait(false);

        var core = await GetSettingValuesAsync(planGuid, CoreGuid, token).ConfigureAwait(false);
        var idle = await GetSettingValuesAsync(planGuid, IdleGuid, token).ConfigureAwait(false);
        var mode = ResolveMode(core, idle);
        return new ModeSnapshot(mode, core, idle);
    }

    public async Task ToggleModeAsync(string planGuid, string planName, CoolIdleTier tier = CoolIdleTier.Balanced, CancellationToken token = default)
    {
        var snapshot = await GetModeSnapshotAsync(planGuid, token).ConfigureAwait(false);
        var target = snapshot.Mode == ParkMode.CoolIdle ? ParkMode.AlwaysOn : ParkMode.CoolIdle;
        await SetModeAsync(planGuid, planName, target, tier, token).ConfigureAwait(false);
    }

    public async Task SetModeAsync(string planGuid, string planName, ParkMode mode, CoolIdleTier tier = CoolIdleTier.Balanced, CancellationToken token = default)
    {
        await EnsureCpuParkingSettingsVisibleAsync(token).ConfigureAwait(false);

        var cleanPlan = planGuid.Trim().Trim('{', '}');
        var (coreValues, idleValues, maxPerfValues, perfIncTimeValues, cpIncreasePolValues) = mode switch
        {
            ParkMode.CoolIdle => tier switch
            {
                CoolIdleTier.MaxCool => (
                    new PowerSettingValues(0, 0),
                    new PowerSettingValues(20, 20),
                    new PowerSettingValues(85, 85),
                    new PowerSettingValues(5, 5),   // Delay unparking (5 time check intervals)
                    new PowerSettingValues(1, 1)    // Single core unpark policy
                ),
                CoolIdleTier.Responsive => (
                    new PowerSettingValues(25, 25),
                    new PowerSettingValues(10, 10),
                    new PowerSettingValues(100, 100),
                    new PowerSettingValues(1, 1),
                    new PowerSettingValues(0, 0)
                ),
                _ => ( // Balanced
                    new PowerSettingValues(0, 0),
                    new PowerSettingValues(20, 20),
                    new PowerSettingValues(99, 99),
                    new PowerSettingValues(3, 3),   // Moderate unpark delay
                    new PowerSettingValues(1, 1)    // Single core unpark policy
                )
            },
            ParkMode.AlwaysOn => (
                new PowerSettingValues(100, 100),
                new PowerSettingValues(0, 0),
                new PowerSettingValues(100, 100),
                new PowerSettingValues(1, 1),
                new PowerSettingValues(0, 0)
            ),
            _ => throw new ArgumentException("Mode must be CoolIdle or AlwaysOn.", nameof(mode)),
        };

        await SetSettingValuesAsync(cleanPlan, "SUB_PROCESSOR", CoreGuid, coreValues, token).ConfigureAwait(false);
        await SetSettingValuesAsync(cleanPlan, "SUB_PROCESSOR", IdleGuid, idleValues, token).ConfigureAwait(false);
        await SetSettingValuesAsync(cleanPlan, "SUB_PROCESSOR", MaxPerfStateGuid, maxPerfValues, token).ConfigureAwait(false);
        await SetSettingValuesAsync(cleanPlan, "SUB_PROCESSOR", MinPerfStateGuid, mode == ParkMode.CoolIdle ? new PowerSettingValues(5, 5) : new PowerSettingValues(100, 100), token).ConfigureAwait(false);
        await SetSettingValuesAsync(cleanPlan, "SUB_PROCESSOR", PerfIncTimeGuid, perfIncTimeValues, token).ConfigureAwait(false);
        await SetSettingValuesAsync(cleanPlan, "SUB_PROCESSOR", CpIncreasePolGuid, cpIncreasePolValues, token).ConfigureAwait(false);

        var activateResult = await RunPowerCfgAsync($"-S {cleanPlan}", token).ConfigureAwait(false);
        EnsureSuccess(activateResult, $"powercfg -S {cleanPlan}");

        var modeLabel = mode == ParkMode.CoolIdle ? $"{ModeToDisplay(mode)} [{TierToDisplay(tier)}]" : ModeToDisplay(mode);
        Log($"Switched to {modeLabel} ({planName})");
    }

    public static string TierToDisplay(CoolIdleTier tier) => tier switch
    {
        CoolIdleTier.MaxCool => "Max Cool (85%)",
        CoolIdleTier.Responsive => "Responsive (100%)",
        _ => "Balanced (99%)"
    };

    public async Task SetActivePlanAsync(string planGuid, string planName, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(planGuid))
        {
            throw new ArgumentException("Plan GUID cannot be empty.", nameof(planGuid));
        }

        var cleanGuid = planGuid.Trim().Trim('{', '}');
        var result = await RunPowerCfgAsync($"/setactive {cleanGuid}", token).ConfigureAwait(false);
        EnsureSuccess(result, $"powercfg /setactive {cleanGuid}");

        Log($"Switched active power plan to {planName} ({cleanGuid})");
    }

    public static string ModeToDisplay(ParkMode mode) => mode switch
    {
        ParkMode.CoolIdle => "Cool Idle",
        ParkMode.AlwaysOn => "Always-On Defaults",
        _ => "Custom (non-standard settings)",
    };

    private static ParkMode ResolveMode(PowerSettingValues core, PowerSettingValues idle)
    {
        if (core.Ac >= 100 && core.Dc >= 100 && idle.Ac <= 0 && idle.Dc <= 0)
        {
            return ParkMode.AlwaysOn;
        }

        if ((core.Ac < 100 || core.Dc < 100) || (idle.Ac > 0 || idle.Dc > 0))
        {
            return ParkMode.CoolIdle;
        }

        return ParkMode.Custom;
    }

    private async Task EnsureCpuParkingSettingsVisibleAsync(CancellationToken token, bool force = false)
    {
        if (!force && _settingsVisibilityEnsured)
        {
            return;
        }

        if (!force && !_settingsVisibilityEnsured && !ShouldRefreshVisibilityMarker())
        {
            _settingsVisibilityEnsured = true;
            return;
        }

        await SettingVisibilityLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!force && _settingsVisibilityEnsured)
            {
                return;
            }

            if (!force && !_settingsVisibilityEnsured && !ShouldRefreshVisibilityMarker())
            {
                _settingsVisibilityEnsured = true;
                return;
            }

            var coreResult = await RunPowerCfgAsync($"/attributes {SubProcessorGuid:D} {CoreGuid:D} -ATTRIB_HIDE", token).ConfigureAwait(false);
            var idleResult = await RunPowerCfgAsync($"/attributes {SubProcessorGuid:D} {IdleGuid:D} -ATTRIB_HIDE", token).ConfigureAwait(false);
            await RunPowerCfgAsync($"/attributes {SubProcessorGuid:D} {MaxPerfStateGuid:D} -ATTRIB_HIDE", token).ConfigureAwait(false);
            await RunPowerCfgAsync($"/attributes {SubProcessorGuid:D} {PerfIncTimeGuid:D} -ATTRIB_HIDE", token).ConfigureAwait(false);
            await RunPowerCfgAsync($"/attributes {SubProcessorGuid:D} {CpIncreasePolGuid:D} -ATTRIB_HIDE", token).ConfigureAwait(false);

            LogVisibilityFailure(CoreGuid, coreResult);
            LogVisibilityFailure(IdleGuid, idleResult);

            var summary = BuildVisibilityErrorSummary((CoreGuid, coreResult), (IdleGuid, idleResult));
            _lastVisibilityError = summary;

            if (summary is null)
            {
                _settingsVisibilityEnsured = true;
                TryWriteVisibilityMarker();
            }
            else
            {
                _settingsVisibilityEnsured = false;
            }
        }
        finally
        {
            SettingVisibilityLock.Release();
        }
    }

    private async Task<PowerSettingValues> GetSettingValuesAsync(string planGuid, Guid settingGuid, CancellationToken token, bool retrying = false)
    {
        var cleanPlan = planGuid.Trim().Trim('{', '}');
        var targetGuid = settingGuid.ToString().ToLowerInvariant();
        var result = await RunPowerCfgAsync($"/query {cleanPlan} {SubProcessorGuid}", token).ConfigureAwait(false);
        EnsureSuccess(result, $"powercfg /query {cleanPlan}");

        var isTarget = false;
        int? ac = null;
        int? dc = null;

        foreach (var rawLine in EnumerateLines(result.StandardOutput))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                if (isTarget && (ac.HasValue || dc.HasValue))
                {
                    break;
                }

                continue;
            }

            if (line.StartsWith("Power Setting GUID", StringComparison.OrdinalIgnoreCase))
            {
                var sections = line.Split(':', 2);
                if (sections.Length < 2)
                {
                    continue;
                }

                var guidToken = sections[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (guidToken is null)
                {
                    continue;
                }

                var candidate = guidToken.Trim().Trim('{', '}').ToLowerInvariant();
                isTarget = candidate == targetGuid;
                if (isTarget)
                {
                    ac = null;
                    dc = null;
                }

                continue;
            }

            if (!isTarget)
            {
                continue;
            }

            var match = HexRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var value = Convert.ToInt32(match.Value.Substring(2), 16);
            var upper = line.ToUpperInvariant();

            if (upper.Contains("AC"))
            {
                ac = value;
            }
            else if (upper.Contains("DC"))
            {
                dc = value;
            }
            else if (!ac.HasValue)
            {
                ac = value;
            }
            else if (!dc.HasValue)
            {
                dc = value;
            }
        }

        if (!ac.HasValue && !dc.HasValue)
        {
            if (!retrying)
            {
                await EnsureCpuParkingSettingsVisibleAsync(token, force: true).ConfigureAwait(false);
                return await GetSettingValuesAsync(planGuid, settingGuid, token, retrying: true).ConfigureAwait(false);
            }

            var command = $"powercfg /attributes SUB_PROCESSOR {settingGuid:D} -ATTRIB_HIDE";
            var companion = settingGuid == CoreGuid ? IdleGuid : CoreGuid;
            var builder = new StringBuilder();
            builder.Append($"Unable to read power settings for {settingGuid:D}. Windows may have hidden this setting again.");
            builder.Append($" Run Park Toggle as administrator or execute `{command}` from an elevated command prompt.");
            builder.Append($" Repeat the command for `{companion:D}` to unhide the companion setting.");

            if (!string.IsNullOrWhiteSpace(_lastVisibilityError))
            {
                builder.Append($" Last unhide attempt reported: {_lastVisibilityError}.");
            }

            var message = builder.ToString();
            Log(message, "ERROR");
            throw new InvalidOperationException(message);
        }

        var resolvedAc = ac ?? dc ?? 0;
        var resolvedDc = dc ?? ac ?? 0;

        return new PowerSettingValues(resolvedAc, resolvedDc);
    }

    private static bool ShouldRefreshVisibilityMarker()
    {
        try
        {
            if (!File.Exists(VisibilityMarkerPath))
            {
                return true;
            }

            if (VisibilityMarkerTtl <= TimeSpan.Zero)
            {
                return false;
            }

            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(VisibilityMarkerPath);
            return age >= VisibilityMarkerTtl;
        }
        catch
        {
            return true;
        }
    }

    private static void TryWriteVisibilityMarker()
    {
        try
        {
            var directory = Path.GetDirectoryName(VisibilityMarkerPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(VisibilityMarkerPath, DateTime.UtcNow.ToString("O"), Encoding.UTF8);
        }
        catch
        {
            // ignore marker failures
        }
    }

    private static string ResolveVisibilityMarkerPath()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(root))
            {
                return Path.Combine(root, "ParkToggle", "power-settings-visible.marker");
            }
        }
        catch
        {
        }

        try
        {
            return Path.Combine(AppContext.BaseDirectory, "power-settings-visible.marker");
        }
        catch
        {
            return "power-settings-visible.marker";
        }
    }

    private void LogVisibilityFailure(Guid settingGuid, ProcessResult result)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var reason = string.IsNullOrWhiteSpace(result.StandardError)
            ? $"exit code {result.ExitCode}"
            : result.StandardError.Trim();

        Log($"powercfg /attributes -ATTRIB_HIDE for {settingGuid:D} failed: {reason}", "WARN");
    }

    private static string? BuildVisibilityErrorSummary(params (Guid Setting, ProcessResult Result)[] attempts)
    {
        var failures = new List<string>();
        foreach (var (setting, result) in attempts)
        {
            if (result.ExitCode == 0)
            {
                continue;
            }

            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"exit code {result.ExitCode}"
                : result.StandardError.Trim();

            failures.Add($"{setting:D}: {detail}");
        }

        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    private async Task SetSettingValuesAsync(string planGuid, string subGroup, Guid settingGuid, PowerSettingValues values, CancellationToken token)
    {
        var guidString = settingGuid.ToString("D");
        var acResult = await RunPowerCfgAsync($"/setacvalueindex {planGuid} {subGroup} {guidString} {values.Ac}", token).ConfigureAwait(false);
        EnsureSuccess(acResult, $"powercfg /setacvalueindex {guidString}");

        var dcResult = await RunPowerCfgAsync($"/setdcvalueindex {planGuid} {subGroup} {guidString} {values.Dc}", token).ConfigureAwait(false);
        EnsureSuccess(dcResult, $"powercfg /setdcvalueindex {guidString}");
    }

    private static Encoding ResolveConsoleEncoding()
    {
        try
        {
            var codePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            return Encoding.GetEncoding(codePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private static IEnumerable<string> EnumerateLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            yield return line;
        }
    }

    private async Task<ProcessResult> RunPowerCfgAsync(string arguments, CancellationToken token)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powercfg",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding,
        };

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start powercfg with arguments \"{arguments}\".");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(token).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var message = $"{operation} failed with exit code {result.ExitCode}. {result.StandardError}".Trim();
        Log(message, "ERROR");
        throw new InvalidOperationException(message);
    }

    public void Log(string message, string level = "INFO")
    {
        try
        {
            lock (_logSync)
            {
                if (!_logInitialized || !File.Exists(_logPath))
                {
                    File.WriteAllText(_logPath, $"==== ParkToggle Log Started {DateTime.Now:G} ====\r\n", Encoding.UTF8);
                    _logInitialized = true;
                }

                File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}\r\n", Encoding.UTF8);
            }
        }
        catch
        {
            // ignore logging failure
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
















