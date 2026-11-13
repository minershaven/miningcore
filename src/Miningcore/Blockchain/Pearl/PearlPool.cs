using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Blockchain.Bitcoin;        // BitcoinStratumMethods (set_difficulty, notify)
using Miningcore.Blockchain.Pearl.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Pearl;

/// <summary>
/// Pearl Stratum pool server.
///
/// Miners connect and speak standard Bitcoin Stratum JSON-RPC:
///   mining.subscribe  →  returns [sessionId, extraNonce1, extraNonce2Size]
///   mining.authorize  →  validates the miner address
///   mining.submit     →  [jobId, plainProofBase64]  (serialized PlainProof)
///
/// The pool then routes the submission through PearlJobManager which calls
/// the Python bridge for proof validation and ZK proof generation.
/// </summary>
[CoinFamily(CoinFamily.Pearl)]
public class PearlPool : PoolBase
{
    public PearlPool(
        IComponentContext            ctx,
        JsonSerializerSettings       serializerSettings,
        IConnectionFactory           cf,
        IStatsRepository             statsRepo,
        IMapper                      mapper,
        IMasterClock                 clock,
        IMessageBus                  messageBus,
        RecyclableMemoryStreamManager rmsm,
        NicehashService              nicehashService) :
        base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus, rmsm, nicehashService)
    {
    }

    private object           currentJobParams;
    private PearlJobManager   manager;
    private PearlPoolConfigExtra extraPoolConfig;
    private PearlCoinTemplate coin;

    // -------------------------------------------------------------------------
    // Stratum handlers
    // -------------------------------------------------------------------------

    private async Task OnSubscribeAsync(
        StratumConnection                connection,
        Timestamped<JsonRpcRequest>      tsRequest)
    {
        var request = tsRequest.Value;

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var context   = connection.ContextAs<PearlWorkerContext>();

        // Params may be an array ["agent", ...] or an object {"agent": "..."}.
        string userAgent = null;
        switch(request.Params)
        {
            case JArray arr when arr.Count > 0:
                userAgent = arr[0]?.Value<string>();
                break;
            case JObject obj:
                userAgent = obj.Value<string>("agent");
                break;
        }

        context.UserAgent = userAgent?.Trim() ?? string.Empty;

        var subscriberData = manager.GetSubscriberData(connection);

        // Response: [[["mining.notify", sessionId]], extraNonce1, extraNonce2Size]
        var data = new object[]
        {
            new object[]
            {
                new object[] { PearlConstants.StratumNotify, connection.ConnectionId }
            }
        }
        .Concat(subscriberData)
        .ToArray();

        await connection.RespondAsync(new JsonRpcResponse<object[]>(data, request.Id));

        context.IsSubscribed = true;

        // Nicehash static diff
        var nicehashDiff = await GetNicehashStaticMinDiff(context, coin.Name, coin.GetAlgorithmName());
        if(nicehashDiff.HasValue)
        {
            logger.Info(() =>
                $"[{connection.ConnectionId}] Nicehash detected. " +
                $"Using API-supplied difficulty {nicehashDiff.Value}");
            context.VarDiff = null;
            context.SetDifficulty(nicehashDiff.Value);
        }
    }

    private async Task OnAuthorizeAsync(
        StratumConnection                connection,
        Timestamped<JsonRpcRequest>      tsRequest,
        CancellationToken                ct)
    {
        var request = tsRequest.Value;

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var context = connection.ContextAs<PearlWorkerContext>();

        // -------------------------------------------------------------------
        // Parse params. lpminer sends an OBJECT:
        //   {"wallet":"ADDRESS.worker","worker":"rig1","agent":"lpminer/0.1.10"}
        // Standard stratum sends an ARRAY: ["ADDRESS.worker", "password"]
        // Support both.
        // -------------------------------------------------------------------
        string walletValue = null;   // raw login/wallet, may contain "ADDRESS.worker"
        string workerField = null;   // explicit worker name (object form)
        string agentValue  = null;   // user-agent (object form)
        string password    = null;   // password (array form)

        switch(request.Params)
        {
            case JObject obj:
                walletValue = obj.Value<string>("wallet")
                    ?? obj.Value<string>("login")
                    ?? obj.Value<string>("user");
                workerField = obj.Value<string>("worker");
                agentValue  = obj.Value<string>("agent");
                password    = obj.Value<string>("pass") ?? obj.Value<string>("password");
                break;

            case JArray arr:
                walletValue = arr.Count > 0 ? arr[0]?.Value<string>() : null;
                password    = arr.Count > 1 ? arr[1]?.Value<string>() : null;
                break;

            default:
                // Fallback: try the generic string[] accessor
                var reqParams = request.ParamsAs<string[]>();
                walletValue = reqParams?.Length > 0 ? reqParams[0] : null;
                password    = reqParams?.Length > 1 ? reqParams[1] : null;
                break;
        }

        var passParts = password?.Split(PasswordControlVarsSeparator);

        // Split "ADDRESS.worker" -> miner + worker suffix
        var split      = walletValue?.Split('.');
        var minerName  = split?.FirstOrDefault()?.Trim();
        var suffixName = split?.Skip(1).FirstOrDefault()?.Trim();

        // Prefer the explicit "worker" field (object form), else the dot-suffix
        var workerName = !string.IsNullOrEmpty(workerField)
            ? workerField.Trim()
            : (suffixName ?? string.Empty);

        if(!string.IsNullOrEmpty(agentValue))
            context.UserAgent = agentValue.Trim();

        // -------------------------------------------------------------------
        // Validate the mining address against the node
        // -------------------------------------------------------------------
        context.IsAuthorized = await manager.ValidateAddressAsync(minerName ?? string.Empty, ct);
        context.Miner  = minerName ?? string.Empty;
        context.Worker = workerName;

        if(!context.IsAuthorized)
        {
            await connection.RespondErrorAsync(
                StratumError.UnauthorizedWorker, "Authorization failed", request.Id, false);

            if(clusterConfig?.Banning?.BanOnLoginFailure is null or true)
            {
                logger.Info(() =>
                    $"[{connection.ConnectionId}] Banning unauthorized worker {minerName} " +
                    $"for {loginFailureBanTimeout.TotalSeconds}s");

                banManager.Ban(connection.RemoteEndpoint.Address, loginFailureBanTimeout);
                Disconnect(connection);
            }

            return;
        }

        // -------------------------------------------------------------------
        // Auto-subscribe if the miner authorized without subscribing first
        // (lpminer does this). This assigns extraNonce1 and puts the
        // connection into a work-ready state.
        // -------------------------------------------------------------------
        if(!context.IsSubscribed)
        {
            // GetSubscriberData assigns context.ExtraNonce1
            manager.GetSubscriberData(connection);
            context.IsSubscribed = true;

            logger.Info(() =>
                $"[{connection.ConnectionId}] Auto-subscribed worker on authorize " +
                $"(no prior mining.subscribe)");
        }

        // Static diff from password ("d=N" / "x=N", see Pearl stratum spec §6)
        var staticDiff = GetStaticDiffFromPassparts(passParts);
        if(staticDiff.HasValue)
        {
            var minDiff = context.VarDiff?.Config.MinDiff ?? 0;
            if(staticDiff.Value < minDiff)
                staticDiff = minDiff;

            context.VarDiff = null;
            context.SetDifficulty(staticDiff.Value);
            logger.Info(() =>
                $"[{connection.ConnectionId}] Static difficulty set to {staticDiff.Value}");
        }

        // -------------------------------------------------------------------
        // Pearl stratum (suprnova dialect, spoken by SRBMiner-MULTI and others):
        // push the first mining.notify BEFORE the authorize ack. There is no
        // mining.set_difficulty; the share target travels inside each notify.
        // -------------------------------------------------------------------
        var jobParams = CreateWorkerJob(connection, true);
        await connection.NotifyAsync(PearlConstants.StratumNotify, jobParams);

        await connection.RespondAsync(new JsonRpcResponse<object>(true, request.Id));
        logger.Info(() => $"[{connection.ConnectionId}] Authorized worker {walletValue} [{context.UserAgent}]");
    }

    private async Task OnSubmitAsync(
        StratumConnection                connection,
        Timestamped<JsonRpcRequest>      tsRequest,
        CancellationToken                ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<PearlWorkerContext>();

        try
        {
            if(request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            var requestAge = clock.Now - tsRequest.Timestamp.UtcDateTime;
            if(requestAge > maxShareAge)
            {
                logger.Warn(() =>
                    $"[{connection.ConnectionId}] Dropping stale share (server overloaded?)");
                return;
            }

            context.LastActivity = clock.Now;

            if(!context.IsAuthorized)
                throw new StratumException(StratumError.UnauthorizedWorker, "unauthorized");

            var share = await manager.SubmitShareAsync(connection, request.Params, ct);

            await connection.RespondAsync(new JsonRpcResponse<object>(true, request.Id));

            messageBus.SendMessage(share);
            PublishTelemetry(TelemetryCategory.Share,
                clock.Now - tsRequest.Timestamp.UtcDateTime, true);

            logger.Info(() =>
                $"[{connection.ConnectionId}] Share accepted: D={Math.Round(share.Difficulty, 3)}");

            if(share.IsBlockCandidate)
                poolStats.LastPoolBlockTime = clock.Now;

            context.Stats.ValidShares++;
            await UpdateVarDiffAsync(connection, false, ct);
        }
        catch(StratumException ex)
        {
            PublishTelemetry(TelemetryCategory.Share,
                clock.Now - tsRequest.Timestamp.UtcDateTime, false);

            context.Stats.InvalidShares++;
            logger.Info(() =>
                $"[{connection.ConnectionId}] Share rejected: {ex.Message} [{context.UserAgent}]");
            PublishRejectedShare(connection, context, ex.Message);

            ConsiderBan(connection, context, poolConfig.Banning);
            throw;
        }
    }

    private async Task OnNewJobAsync(object jobParams)
    {
        currentJobParams = jobParams;

        var jobMap   = jobParams as IReadOnlyDictionary<string, object>;
        var jobId    = jobMap != null && jobMap.TryGetValue("job_id", out var jid) ? jid : null;
        var cleanJob = jobMap != null && jobMap.TryGetValue("clean_jobs", out var cj) && cj is bool b && b;

        logger.Info(() => $"Broadcasting job {jobId}");

        await Guard(() => ForEachMinerAsync(async (connection, ct) =>
        {
            var context      = connection.ContextAs<PearlWorkerContext>();

            if(!context.IsAuthorized)
                return;

            context.ApplyPendingDifficulty();
            var minerParams  = CreateWorkerJob(connection, cleanJob);
            await connection.NotifyAsync(PearlConstants.StratumNotify, minerParams);
        }));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private object CreateWorkerJob(StratumConnection connection, bool cleanJob)
    {
        var context      = connection.ContextAs<PearlWorkerContext>();
        var maxJobs      = extraPoolConfig?.MaxActiveJobs ?? 4;
        var job          = manager.GetJobForStratum();

        lock(context)
            context.AddJob(job, maxJobs);

        // Inject the per-worker stratum difficulty (port default / static / vardiff)
        return job.GetJobParams(cleanJob, context.Difficulty);
    }

    // -------------------------------------------------------------------------
    // PoolBase overrides
    // -------------------------------------------------------------------------

    public override double HashrateFromShares(double shares, double interval) =>
    shares * 4294967296d / interval;

    public override double ShareMultiplier => 1;

    /// <summary>
    /// Pearl mining.submit carries a serialized PlainProof which far exceeds
    /// the default 32 KB stratum line limit (SRBMiner H100 proofs ~1.5 MB, OneZeroMiner
    /// up to ~2.9 MB base64). Default to 9 MiB, configurable
    /// via the pool extra "maxStratumRequestSize".
    /// </summary>
    protected override int MaxInboundRequestLength =>
        extraPoolConfig?.MaxStratumRequestSize ?? 0x900000;

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin            = pc.Template.As<PearlCoinTemplate>();
        extraPoolConfig = pc.Extra.SafeExtensionDataAs<PearlPoolConfigExtra>() ?? new();
        base.Configure(pc, cc);
    }

    protected override async Task SetupJobManager(CancellationToken ct)
    {
        var en1Size = extraPoolConfig?.ExtraNonce1Size ?? 4;

        manager = ctx.Resolve<PearlJobManager>(
            new TypedParameter(typeof(IExtraNonceProvider),
                new PearlExtraNonceProvider(poolConfig.Id, en1Size, clusterConfig.InstanceId)));

        manager.Configure(poolConfig, clusterConfig);
        await manager.StartAsync(ct);

        if(poolConfig.EnableInternalStratum == true)
        {
            disposables.Add(manager.Jobs
                .Select(job => Observable.FromAsync(() =>
                    Guard(() => OnNewJobAsync(job),
                        ex => logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}"))))
                .Concat()
                .Subscribe(_ => { }, ex => logger.Debug(ex, nameof(OnNewJobAsync))));

            await manager.Jobs.Take(1).ToTask(ct);
        }
        else
        {
            disposables.Add(manager.Jobs.Subscribe());
        }
    }

    protected override async Task InitStatsAsync(CancellationToken ct)
    {
        await base.InitStatsAsync(ct);
        blockchainStats = manager.BlockchainStats;
    }

    protected override WorkerContextBase CreateWorkerContext() =>
        new PearlWorkerContext();

    protected override async Task OnRequestAsync(
        StratumConnection           connection,
        Timestamped<JsonRpcRequest> tsRequest,
        CancellationToken           ct)
    {
        var request = tsRequest.Value;
        try
        {
            switch(request.Method)
            {
                case PearlConstants.StratumSubscribe:
                    await OnSubscribeAsync(connection, tsRequest);
                    break;

                case PearlConstants.StratumAuthorize:
                    await OnAuthorizeAsync(connection, tsRequest, ct);
                    break;

                case PearlConstants.StratumSubmit:
                    await OnSubmitAsync(connection, tsRequest, ct);
                    break;

                default:
                    logger.Debug(() =>
                        $"[{connection.ConnectionId}] Unsupported request: " +
                        $"{JsonConvert.SerializeObject(request, serializerSettings)}");
                    await connection.RespondErrorAsync(
                        StratumError.Other, $"Unsupported method {request.Method}", request.Id);
                    break;
            }
        }
        catch(StratumException ex)
        {
            await connection.RespondErrorAsync(ex.Code, ex.Message, request.Id, false);
        }
    }

    protected override async Task OnVarDiffUpdateAsync(
        StratumConnection connection, double newDiff, CancellationToken ct)
    {
        await base.OnVarDiffUpdateAsync(connection, newDiff, ct);

        if(connection.Context.ApplyPendingDifficulty())
        {
            // Re-issue work at the new difficulty. Use cleanJobs=false so the
            // miner keeps any in-flight work valid where possible.
            var minerParams  = CreateWorkerJob(connection, false);
            await connection.NotifyAsync(PearlConstants.StratumNotify, minerParams);
        }
    }
}
