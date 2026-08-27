using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.KGSM.Watchdog.Tests;

/// <summary>
/// A fake <see cref="IInstanceService"/> answering only <see cref="GetInstanceInfo"/>, from a map a test
/// fills in, and counting the calls so a test can assert exactly-once resolution. Every other member
/// throws: the ingesters under test call nothing else, and a stub that quietly returned a default would
/// let a test pass over a call the component should never make.
/// </summary>
internal sealed class FakeInstanceService : IInstanceService
{
    private readonly Dictionary<string, Instance> _byName = new(StringComparer.Ordinal);
    public int CallCount { get; private set; }
    public void Add(Instance i) => _byName[i.Name] = i;

    public Instance? GetInstanceInfo(string instanceName)
    {
        CallCount++;
        return _byName.TryGetValue(instanceName, out var i) ? i : null;
    }

    // ---- unused by the ingester ----
    public Dictionary<string, Instance> GetAll() => throw new NotImplementedException();
    public Dictionary<string, Instance>? GetAllOrNull() => throw new NotImplementedException();
    public InstanceRuntimeStatus? GetInstanceStatus(string instanceName) => throw new NotImplementedException();
    public Dictionary<string, Reading<InstanceRuntimeStatus>> GetAllStatuses(bool fast = false) => throw new NotImplementedException();
    public KgsmResult Install(string blueprintName, string? library = null, string? version = null, string? displayName = null, string? actor = null, string? origin = null, int? port = null, bool? start = null, string? id = null) => throw new NotImplementedException();
    public KgsmResult Move(string instanceName, string library, bool skipSpaceCheck = false, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Announce(string instanceName, string message, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult SetDisplayName(string instanceId, string displayName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Uninstall(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public ICollection<string> GetLogs(string instanceName, int maxLines = 10) => throw new NotImplementedException();
    public Task<ICollection<string>> GetLogsAsync(string instanceName, int maxLines = 10, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public KgsmResult GetStatus(string instanceName) => throw new NotImplementedException();
    public KgsmResult GetInfo(string instanceName) => throw new NotImplementedException();
    public bool IsActive(string instanceName) => throw new NotImplementedException();
    public KgsmResult Start(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Stop(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Restart(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult GetInstalledVersion(string instanceName) => throw new NotImplementedException();
    public KgsmResult GetLatestVersion(string instanceName) => throw new NotImplementedException();
    public KgsmResult CheckUpdate(string instanceName, bool emit = false, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult DeleteBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult Update(string instanceName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult GetBackups(string instanceName) => throw new NotImplementedException();
public List<InstanceBackup> GetBackupsDetailed(string instanceName) => throw new NotImplementedException();
public InstanceNoteResult SetInstanceNote(string instanceName, string body, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult CreateBackup(string instanceName, string? actor = null, string? origin = null, string? reason = null, string? retention = null) => throw new NotImplementedException();
    public KgsmResult PinBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult UnpinBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public List<TheKrystalShip.KGSM.Core.Models.InstanceConfigEntry>? GetInstanceConfig(string instanceName, bool settableOnly = false) => throw new NotImplementedException();
    public KgsmResult RestoreBackup(string instanceName, string backupName, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult PruneBackups(string instanceName, int keepN, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult GenerateId(string blueprintName, string? customName = null) => throw new NotImplementedException();
    public KgsmResult Save(string instanceName) => throw new NotImplementedException();
    public KgsmResult SendInput(string instanceName, string command, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public KgsmResult FindConfigPath(string instanceName) => throw new NotImplementedException();
    public KgsmResult GetInstanceConfigValue(string instanceName, string key) => throw new NotImplementedException();
    public KgsmResult SetInstanceConfigValue(string instanceName, string key, string value, string? actor = null, string? origin = null) => throw new NotImplementedException();
    public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    public Task<LogSubscription> SubscribeToLogsAsync(string instanceName, TheKrystalShip.KGSM.Core.Models.Enums.LogLevel minimumLogLevel, bool includeRawLines = true, CancellationToken cancellationToken = default) => throw new NotImplementedException();

public KgsmResult Kick(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
public KgsmResult Ban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
public KgsmResult Unban(string instanceName, string target, string? actor = null, string? origin = null) => throw new NotImplementedException();
}
