using System.Numerics;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Autofac;
using Miningcore.Blockchain.Pearl.Configuration;
using Miningcore.Blockchain.Pearl.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Pearl;

/// <summary>
/// Pearl job manager.
///
/// Talks to pearld via standard btcd JSON-RPC over (optionally TLS) HTTP:
///   - getblockchaininfo  (health / sync / network stats)
///   - getblocktemplate   (work source)
///   - getpeerinfo        (connectivity)
///   - submitblock        (block submission)
///
/// Pearl-specific proof generation and block serialization are delegated to
/// the pearl_mining / pearl-gateway Python code via PearlPythonBridge, so the
/// produced block bytes stay byte-identical to the upstream gateway.
/// </summary>
public class PearlJobManager : JobManagerBase<PearlJob>
{
    public PearlJobManager(
        IComponentContext   ctx,
        IMasterClock        clock,
        IMessageBus         messageBus,
        IExtraNonceProvider extraNonceProvider) :
        base(ctx, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(messageBus);
        Contract.RequiresNonNull(extraNonceProvider);

        this.clock = clock;
        this.extraNonceProvider = extraNonceProvider;
    }

    private readonly IMasterClock        clock;
    private readonly IExtraNonceProvider extraNonceProvider;

    private RpcClient            rpc;
    private PearlCoinTemplate    coin;
    private PearlPoolConfigExtra extraConfig;
    private DaemonEndpointConfig[] daemonEndpoints = Array.Empty<DaemonEndpointConfig>();
    private PearlPythonBridge    bridge;

    private string networkType = "mainnet";
    protected int  maxActiveJobs;

    public IObservable<object> Jobs { get; private set; } = Observable.Never<object>();
    public BlockchainStats     BlockchainStats { get; } = new();
    public PearlCoinTemplate   Coin => coin;

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin        = pc.Template.As<PearlCoinTemplate>();
        extraConfig = pc.Extra.SafeExtensionDataAs<PearlPoolConfigExtra>() ?? new();
        maxActiveJobs = extraConfig.MaxActiveJobs ?? 4;

        daemonEndpoints = pc.Daemons
            .Where(x => string.IsNullOrEmpty(x.Category))
            .ToArray();

        base.Configure(pc, cc);
    }

    public object[] GetSubscriberData(StratumConnection worker)
    {
        Contract.RequiresNonNull(worker);
        var context = worker.ContextAs<PearlWorkerContext>();
        var en1Size = extraConfig.ExtraNonce1Size ?? 4;

        context.ExtraNonce1 = extraNonceProvider.Next();

        return new object[]
        {
            context.ExtraNonce1,
            PearlConstants.ExtraNoncePlaceholderLength - en1Size,
        };
    }

    public int GetExtraNonce1Size() => extraConfig.ExtraNonce1Size ?? 4;

    public override PearlJob GetJobForStratum() => currentJob;

    public object GetJobParamsForStratum(bool isNew) => currentJob?.GetJobParams(isNew);

    // -------------------------------------------------------------------------
    // Share submission
    // -------------------------------------------------------------------------

    public async ValueTask<Share> SubmitShareAsync(
        StratumConnection worker, object submission, CancellationToken ct)
    {
        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(submission);

        var context = worker.ContextAs<PearlWorkerContext>();

        // Submit params may arrive as:
        //   - object: {"id"/"jobId": "...", "plainProof"/"proof": "<base64>"}  (lpminer style)
        //   - array:  [jobId, plainProofBase64]                                (standard stratum)
        string jobId          = null;
        string plainProofData = null;

        switch(submission)
        {
            case JObject obj:
                jobId = obj.Value<string>("jobId")
                    ?? obj.Value<string>("id")
                    ?? obj.Value<string>("job_id");
                plainProofData = obj.Value<string>("plainProof")
                    ?? obj.Value<string>("plain_proof")
                    ?? obj.Value<string>("proof")
                    ?? obj.Value<string>("data");
                break;

            case JArray arr:
                jobId          = arr.Count > 0 ? arr[0]?.Value<string>() : null;
                plainProofData = arr.Count > 1 ? arr[1]?.Value<string>() : null;
                break;

            case string[] strParams:
                jobId          = strParams.Length > 0 ? strParams[0] : null;
                plainProofData = strParams.Length > 1 ? strParams[1] : null;
                break;

            case object[] submitParams:
                jobId          = submitParams.Length > 0 ? submitParams[0] as string : null;
                plainProofData = submitParams.Length > 1 ? submitParams[1] as string : null;
                break;

            default:
                throw new StratumException(StratumError.Other, "invalid params");
        }

        if(string.IsNullOrEmpty(jobId) || string.IsNullOrEmpty(plainProofData))
        {
            // Diagnostic: log the shape of what the miner actually sent
            // (field names and sizes only - never the payload itself).
            logger.Warn(() => $"mining.submit unparsable: {DescribeSubmission(submission)}");
            throw new StratumException(StratumError.Other, "missing submit params");
        }

        logger.Debug(() =>
            $"mining.submit jobId={jobId} proofLen={plainProofData.Length} " +
            $"shape=({DescribeSubmission(submission)})");

        // Strip the "_<difficulty>" suffix added to job ids in mining.notify.
        var us = jobId.IndexOf('_');
        if(us > 0)
            jobId = jobId[..us];

        PearlJob job;
        lock(context)
            job = context.GetJob(jobId);

        if(job == null)
            throw new StratumException(StratumError.JobNotFound, PearlStratumErrors.StaleJob);

        var (share, blockHex) = await job.ProcessShareAsync(
            worker, plainProofData, bridge, clock, ct);

        share.PoolId    = poolConfig.Id;
        share.IpAddress = worker.RemoteEndpoint.Address.ToString();
        share.Miner     = context.Miner;
        share.Worker    = context.Worker;
        share.UserAgent = context.UserAgent;
        share.Source    = clusterConfig.ClusterName;
        share.Created   = clock.Now;

        if(share.IsBlockCandidate)
        {
            logger.Info(() => $"Submitting block {share.BlockHeight} [{share.BlockHash}]");

            var accepted = await SubmitBlockAsync(share, blockHex, ct);
            share.IsBlockCandidate = accepted;

            if(accepted)
            {
                logger.Info(() =>
                    $"Daemon accepted block {share.BlockHeight} [{share.BlockHash}] " +
                    $"submitted by {context.Miner}");

                OnBlockFound();
                share.TransactionConfirmationData = share.BlockHash;
            }
            else
            {
                share.TransactionConfirmationData = null;
            }
        }

        return share;
    }

    /// <summary>
    /// Describes a submit payload's structure for diagnostics: field names and
    /// value lengths only - never the actual proof bytes.
    /// </summary>
    private static string DescribeSubmission(object submission)
    {
        switch(submission)
        {
            case JObject obj:
                return "object{" + string.Join(", ", obj.Properties()
                    .Select(p => $"{p.Name}:{(p.Value.Type == JTokenType.String ? $"str[{p.Value.Value<string>()?.Length}]" : p.Value.Type.ToString())}")) + "}";

            case JArray arr:
                return "array[" + string.Join(", ", arr
                    .Select(t => t.Type == JTokenType.String ? $"str[{t.Value<string>()?.Length}]" : t.Type.ToString())) + "]";

            case object[] oa:
                return $"object[]({oa.Length})";

            default:
                return submission?.GetType().Name ?? "null";
        }
    }

    public async Task<bool> ValidateAddressAsync(string address, CancellationToken ct)
    {
        if(string.IsNullOrEmpty(address))
            return false;

        var response = await rpc.ExecuteAsync<PearlValidateAddressResponse>(
            logger, PearlConstants.RpcValidateAddress, ct, new[] { (object) address });

        return response?.Response?.IsValid == true;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<bool> UpdateJobAsync(CancellationToken ct, string via = null)
    {
        try
        {
            // getblocktemplate with segwit rules (matches pearl-gateway)
            var gbtRequest = new
            {
                capabilities = new[] { "coinbasevalue", "workid", "coinbase/append" },
                rules        = new[] { "segwit" },
            };

            var response = await rpc.ExecuteAsync<PearlBlockTemplate>(
                logger, PearlConstants.RpcGetBlockTemplate, ct, new[] { (object) gbtRequest });

            if(response.Error != null || response.Response == null)
            {
                logger.Warn(() => $"getblocktemplate failed: {response.Error?.Message}");
                return false;
            }

            var template = response.Response;
            var job      = currentJob;
            var isNew    = job == null || job.PreviousBlockHash != template.PreviousBlockHash;

            if(isNew)
            {
                messageBus.NotifyChainHeight(poolConfig.Id, template.Height, poolConfig.Template);

                var newJob = new PearlJob();
                newJob.Init(template, NextJobId(), clock, poolConfig.Address, bridge);

                // Hand the template to the bridge so it can build the incomplete
                // header bytes and later assemble the block.
                await bridge.SetTemplateAsync(newJob.JobId, template, ct);

                logger.Info(() => via != null
                    ? $"Detected new block {template.Height} [{via}]"
                    : $"Detected new block {template.Height}");

                if(template.Height > BlockchainStats.BlockHeight)
                {
                    BlockchainStats.LastNetworkBlockTime = clock.Now;
                    BlockchainStats.BlockHeight          = template.Height;
                }

                currentJob = newJob;
            }
            else
            {
                logger.Debug(() => via != null
                    ? $"Template update {template.Height} [{via}]"
                    : $"Template update {template.Height}");
            }

            return isNew;
        }
        catch(OperationCanceledException)
        {
            // ignored
        }
        catch(Exception ex)
        {
            logger.Error(ex, nameof(UpdateJobAsync));
        }

        return false;
    }

    private async Task UpdateNetworkStatsAsync(CancellationToken ct)
    {
        try
        {
            var info = await rpc.ExecuteAsync<PearlBlockchainInfo>(
                logger, PearlConstants.RpcGetBlockchainInfo, ct);

            if(info.Response != null)
            {
                BlockchainStats.BlockHeight       = info.Response.Blocks;
                BlockchainStats.NetworkDifficulty = info.Response.Difficulty;
            }

            // getnetworkhashps can exceed Int64 range; Newtonsoft deserializes such
            // values as BigInteger, which does not implement IConvertible, so
            // JToken.Value<double>() would throw. Read the raw value and cast.
            var hashps = await rpc.ExecuteAsync<JToken>(
                logger, PearlConstants.RpcGetNetworkHashps, ct);
            if(hashps.Response is JValue { Value: BigInteger raw })
                BlockchainStats.NetworkHashrate = (double) raw;

            var peers = await rpc.ExecuteAsync<PearlPeerInfo[]>(
                logger, PearlConstants.RpcGetPeerInfo, ct);
            if(peers.Response != null)
                BlockchainStats.ConnectedPeers = peers.Response.Length;
        }
        catch(Exception ex)
        {
            logger.Error(ex, nameof(UpdateNetworkStatsAsync));
        }
    }

    /// <summary>
    /// submitblock &lt;block_hex&gt; — returns null on success, error string on rejection.
    /// </summary>
    private async Task<bool> SubmitBlockAsync(Share share, string blockHex, CancellationToken ct)
    {
        try
        {
            var response = await rpc.ExecuteAsync<JToken>(
                logger, PearlConstants.RpcSubmitBlock, ct, new[] { (object) blockHex });

            // btcd submitblock: null result = accepted; non-null = reject reason
            if(response.Error != null)
            {
                logger.Warn(() => $"Block {share.BlockHeight} submission RPC error: {response.Error.Message}");
                messageBus.SendMessage(new AdminNotification("Block submission failed",
                    $"Pool {poolConfig.Id} failed to submit block {share.BlockHeight}: {response.Error.Message}"));
                return false;
            }

            if(response.Response != null && response.Response.Type != JTokenType.Null)
            {
                var reason = response.Response.ToString();
                logger.Warn(() => $"Block {share.BlockHeight} rejected: {reason}");
                return false;
            }

            return true;
        }
        catch(Exception ex)
        {
            logger.Error(ex, nameof(SubmitBlockAsync));
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // JobManagerBase overrides
    // -------------------------------------------------------------------------

    protected override void ConfigureDaemons()
    {
        var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();
        rpc = new RpcClient(daemonEndpoints.First(), jsonSerializerSettings, messageBus, poolConfig.Id);
    }

    protected override async Task<bool> AreDaemonsHealthyAsync(CancellationToken ct)
    {
        logger.Debug(() => $"Checking if '{PearlConstants.DaemonName}' is healthy...");

        var response = await rpc.ExecuteAsync<PearlBlockchainInfo>(
            logger, PearlConstants.RpcGetBlockchainInfo, ct);

        if(response.Error != null)
        {
            logger.Warn(() => $"'{PearlConstants.DaemonName}' getblockchaininfo: {response.Error.Message}");
            return false;
        }

        return response.Response != null;
    }

    protected override async Task<bool> AreDaemonsConnectedAsync(CancellationToken ct)
    {
        logger.Debug(() => $"Checking if '{PearlConstants.DaemonName}' is connected...");

        var peers = await rpc.ExecuteAsync<PearlPeerInfo[]>(
            logger, PearlConstants.RpcGetPeerInfo, ct);

        // On regtest/simnet 0 peers is acceptable; on mainnet require >0
        if(networkType is "mainnet")
            return peers.Response?.Length > 0;

        return peers.Error == null;
    }

    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        logger.Debug(() => $"Waiting for '{PearlConstants.DaemonName}' to sync...");

        do
        {
            var info = await rpc.ExecuteAsync<PearlBlockchainInfo>(
                logger, PearlConstants.RpcGetBlockchainInfo, ct);

            if(info.Response != null)
            {
                // Consider synced when verificationprogress is essentially 1
                // or blocks == headers.
                var synced = info.Response.VerificationProgress >= 0.9999 ||
                    (info.Response.Headers > 0 && info.Response.Blocks >= info.Response.Headers);

                if(synced)
                {
                    logger.Info(() => $"'{PearlConstants.DaemonName}' synced with blockchain");
                    break;
                }

                logger.Info(() =>
                    $"Syncing... blocks {info.Response.Blocks}/{info.Response.Headers}. " +
                    "Manager will start once synced.");
            }
            else
            {
                logger.Debug(() => $"'{PearlConstants.DaemonName}' did not respond to getblockchaininfo");
            }
        } while(await timer.WaitForNextTickAsync(ct));
    }

    protected override async Task PostStartInitAsync(CancellationToken ct)
    {
        if(string.IsNullOrEmpty(poolConfig.Address))
            throw new PoolStartupException("Pool address (mining address) is not configured", poolConfig.Id);

        // Determine network type
        var info = await rpc.ExecuteAsync<PearlBlockchainInfo>(
            logger, PearlConstants.RpcGetBlockchainInfo, ct);

        if(info.Response == null)
            throw new PoolStartupException(
                $"Init failed: getblockchaininfo returned no result ({info.Error?.Message})", poolConfig.Id);

        networkType = info.Response.Chain switch
        {
            "mainnet" or "main" => "mainnet",
            "testnet" or "test" => "testnet",
            "simnet"            => "simnet",
            "regtest"           => "regtest",
            _                   => info.Response.Chain ?? "mainnet",
        };

        BlockchainStats.RewardType  = "POW";
        BlockchainStats.NetworkType = networkType;

        // Validate the configured mining address against the node
        var validAddr = await ValidateAddressAsync(poolConfig.Address, ct);
        if(!validAddr)
            logger.Warn(() => $"Configured pool address '{poolConfig.Address}' did not validate against the node");

        // Start the pearl_mining / pearl-gateway Python sidecar
        bridge = new PearlPythonBridge();
        await bridge.StartAsync(
            pythonExe:    string.IsNullOrEmpty(extraConfig.PythonExecutable) ? "python3" : extraConfig.PythonExecutable,
            bridgeScript: string.IsNullOrEmpty(extraConfig.PythonBridgeScript) ? "pearl_mining_bridge.py" : extraConfig.PythonBridgeScript,
            miningAddress: poolConfig.Address,
            ct: ct);

        await UpdateNetworkStatsAsync(ct);

        Observable.Interval(TimeSpan.FromMinutes(1))
            .Select(_ => Observable.FromAsync(() =>
                Guard(() => UpdateNetworkStatsAsync(ct), ex => logger.Error(ex))))
            .Concat()
            .Subscribe();

        SetupJobUpdates(ct);
    }

    protected virtual void SetupJobUpdates(CancellationToken ct)
    {
        var pollingInterval = poolConfig?.BlockRefreshInterval ?? 1000;
        if(pollingInterval <= 0)
            pollingInterval = 1000;

        var blockSubmission = blockFoundSubject.Synchronize();
        var pollTimerRestart = blockFoundSubject.Synchronize();

        var triggers = new List<IObservable<(string Via, string Data)>>
        {
            blockSubmission.Select(_ => (JobRefreshBy.BlockFound, (string) null)),

            // Standard polling of getblocktemplate
            Observable.Timer(TimeSpan.FromMilliseconds(pollingInterval))
                .TakeUntil(pollTimerRestart)
                .Select(_ => (JobRefreshBy.Poll, (string) null))
                .Repeat(),
        };

        Jobs = triggers.Merge()
            .Select(x => Observable.FromAsync(() => UpdateJobAsync(ct, x.Via)))
            .Concat()
            .Where(x => x)
            .Do(x => { if(x) hasInitialBlockTemplate = true; })
            .Select(_ => GetJobParamsForStratum(true))
            .Publish()
            .RefCount();
    }
}
