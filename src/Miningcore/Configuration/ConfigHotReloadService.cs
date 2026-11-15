using System.Collections.Concurrent;
using System.Reflection;
using Autofac;
using Autofac.Features.Metadata;
using Microsoft.Extensions.Hosting;
using Miningcore.Mining;
using Newtonsoft.Json;
using NLog;
using ILogger = NLog.ILogger;

namespace Miningcore.Configuration;

public class ConfigHotReloadService : BackgroundService
{
    public ConfigHotReloadService(
        IComponentContext ctx,
        IHostApplicationLifetime hal,
        ConcurrentDictionary<string, IMiningPool> pools,
        ClusterConfig clusterConfig,
        string configFilePath)
    {
        this.ctx = ctx;
        this.hal = hal;
        this.pools = pools;
        this.clusterConfig = clusterConfig;
        this.configFilePath = configFilePath;
    }

    private readonly IComponentContext ctx;
    private readonly IHostApplicationLifetime hal;
    private readonly ConcurrentDictionary<string, IMiningPool> pools;
    private readonly ClusterConfig clusterConfig;
    private readonly string configFilePath;
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

    private FileSystemWatcher watcher;
    private DateTime lastChange = DateTime.MinValue;
    private bool reloadPending;
    private readonly object reloadLock = new();
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Cts, Task Task)> poolTasks = new();
    private Dictionary<string, CoinTemplate> coinTemplates;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(configFilePath)) ?? ".";
        var file = Path.GetFileName(configFilePath);

        watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };

        watcher.Changed += OnConfigChanged;
        watcher.Created += OnConfigChanged;

        logger.Info(() => $"Config hot-reload active — watching {configFilePath}");

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch(OperationCanceledException)
        {
        }

        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    private void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        lock(reloadLock)
        {
            if(reloadPending)
                return;

            var now = DateTime.UtcNow;
            if((now - lastChange).TotalMilliseconds < 500)
                return;

            lastChange = now;
            reloadPending = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);
                await ReloadPoolsAsync();
            }
            finally
            {
                lock(reloadLock) { reloadPending = false; }
            }
        });
    }

    private async Task ReloadPoolsAsync()
    {
        logger.Info("Config change detected — reloading pools...");

        ClusterConfig newConfig;
        try
        {
            var json = await File.ReadAllTextAsync(configFilePath);
            newConfig = JsonConvert.DeserializeObject<ClusterConfig>(json);
            if(newConfig == null || newConfig.Pools == null)
            {
                logger.Warn("Config reload produced null config — keeping current pools");
                return;
            }
        }
        catch(Exception ex)
        {
            logger.Error(ex, "Failed to reload config — keeping current pools");
            return;
        }

        coinTemplates = LoadCoinTemplatesInstance(newConfig);

        var oldIds = new HashSet<string>(pools.Keys);
        var newIds = new HashSet<string>(newConfig.Pools.Where(p => p.Enabled).Select(p => p.Id));

        var toRemove = oldIds.Except(newIds).ToList();
        var toAdd = newIds.Except(oldIds).ToList();
        var toCheck = oldIds.Intersect(newIds).ToList();

        if(toRemove.Count == 0 && toAdd.Count == 0 && toCheck.Count == 0)
        {
            logger.Debug("No pool changes detected");
            return;
        }

        foreach(var id in toRemove)
        {
            await StopPoolAsync(id);
            pools.TryRemove(id, out _);
            logger.Info($"Pool '{id}' stopped and removed");
        }

        var toRestart = new List<string>();
        foreach(var id in toCheck)
        {
            var oldPool = clusterConfig.Pools.FirstOrDefault(p => p.Id == id);
            var newPool = newConfig.Pools.FirstOrDefault(p => p.Id == id);
            if(oldPool != null && newPool != null && !newPool.Enabled)
            {
                await StopPoolAsync(id);
                pools.TryRemove(id, out _);
                logger.Info($"Pool '{id}' disabled");
                continue;
            }
            if(oldPool != null && newPool != null && PoolConfigChanged(oldPool, newPool))
            {
                toRestart.Add(id);
            }
        }

        foreach(var id in toRestart)
        {
            await StopPoolAsync(id);
            pools.TryRemove(id, out _);
            logger.Info($"Pool '{id}' config changed — restarting");
            toAdd.Add(id);
        }

        foreach(var id in toAdd)
        {
            var poolConfig = newConfig.Pools.First(p => p.Id == id);
            try
            {
                await StartPoolAsync(poolConfig, newConfig);
                logger.Info($"Pool '{id}' started");
            }
            catch(Exception ex)
            {
                logger.Error(ex, $"Failed to start pool '{id}'");
            }
        }

        // Pools that kept running are still using their old PoolConfig objects (with the coin template
        // resolved at startup); keep those, and give every other entry its coin template, so the API,
        // payouts and stats never see a pool whose Template is null.
        var restarted = new HashSet<string>(toAdd);

        clusterConfig.Pools = newConfig.Pools.Select(p =>
        {
            if(p.Enabled && !restarted.Contains(p.Id))
            {
                var running = clusterConfig.Pools.FirstOrDefault(o => o.Id == p.Id);

                if(running?.Template != null)
                    return running;
            }

            if(p.Template == null && p.Coin != null && coinTemplates.TryGetValue(p.Coin, out var template))
                p.Template = template;

            return p;
        }).ToArray();

        clusterConfig.PaymentProcessing = newConfig.PaymentProcessing;
        clusterConfig.Api = newConfig.Api;
        clusterConfig.Banning = newConfig.Banning;
    }

    private static bool PoolConfigChanged(PoolConfig oldPool, PoolConfig newPool)
    {
        var oldJson = JsonConvert.SerializeObject(Normalize(oldPool));
        var newJson = JsonConvert.SerializeObject(Normalize(newPool));
        return oldJson != newJson;
    }

    private static readonly Func<PoolConfig, object> Normalize = p => new
    {
        p.Coin, p.Address,
        p.BlockRefreshInterval, p.JobRebroadcastTimeout,
        p.ClientConnectionTimeout, p.EnableInternalStratum,
        p.PaymentProcessing,
        ExtraClean = StripCommentKeys(p.Extra),
        daemonPorts = p.Daemons?.Select(d => d.Port).ToArray(),
        stratumPorts = p.Ports?.Keys.OrderBy(k => k).ToArray(),
    };

    private static IDictionary<string, object> StripCommentKeys(IDictionary<string, object> extra)
    {
        if(extra == null) return null;
        return extra.Where(kv => !kv.Key.StartsWith("_"))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private async Task StartPoolAsync(PoolConfig poolConfig, ClusterConfig newClusterConfig)
    {
        if(!coinTemplates.TryGetValue(poolConfig.Coin, out var template))
            throw new InvalidOperationException($"Pool {poolConfig.Id} references undefined coin '{poolConfig.Coin}'");

        poolConfig.Template = template;

        var poolImpl = ctx.Resolve<IEnumerable<Meta<Lazy<IMiningPool, CoinFamilyAttribute>>>>()
            .First(x => x.Value.Metadata.SupportedFamilies.Contains(poolConfig.Template.Family)).Value;

        var pool = poolImpl.Value;
        pool.Configure(poolConfig, newClusterConfig);
        pools[poolConfig.Id] = pool;

        var cts = new CancellationTokenSource();
        var task = Task.Run(() => pool.RunAsync(cts.Token), cts.Token);
        poolTasks[poolConfig.Id] = (cts, task);
    }

    private async Task StopPoolAsync(string id)
    {
        if(!poolTasks.TryRemove(id, out var entry))
            return;

        logger.Info($"Stopping pool '{id}'...");
        entry.Cts.Cancel();
        try
        {
            await Task.WhenAny(entry.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        }
        catch
        {
        }
        entry.Cts.Dispose();
    }

    private Dictionary<string, CoinTemplate> LoadCoinTemplatesInstance(ClusterConfig config)
    {
        var basePath = Path.GetDirectoryName(Assembly.GetEntryAssembly()!.Location)!;
        var defaultTemplates = Path.Combine(basePath, "coins.json");

        var paths = new List<string> { defaultTemplates };
        if(config.CoinTemplates != null)
            paths.AddRange(config.CoinTemplates.Where(x => x != defaultTemplates));

        return CoinTemplateLoader.Load(ctx, paths.ToArray());
    }
}
