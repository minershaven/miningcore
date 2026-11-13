using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Configuration;
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
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Quantus;

/// <summary>
/// Quantus (QTC) stratum pool.
///
/// Speaks the XMRig-style protocol that SRBMiner-MULTI uses for --algorithm quantus:
///   login     {"login":"ADDRESS.worker","pass":"x","agent":"..."}  -> {id, job, status, extensions}
///   job       {"clean_jobs":bool,"job":{difficulty,extranonce,job_id,mining_hash,target}}
///   submit    {"id":session,"job_id":"...","nonce":"&lt;64-byte hex, starting with extranonce&gt;"}
///   keepalived
/// </summary>
[CoinFamily(CoinFamily.Quantus)]
public class QuantusPool : PoolBase
{
    public QuantusPool(IComponentContext ctx,
        JsonSerializerSettings serializerSettings,
        IConnectionFactory cf,
        IStatsRepository statsRepo,
        IMapper mapper,
        IMasterClock clock,
        IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm,
        NicehashService nicehashService) :
        base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus, rmsm, nicehashService)
    {
    }

    private QuantusJobManager manager;
    private QuantusCoinTemplate coin;

    private async Task OnLoginAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<QuantusWorkerContext>();

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var loginRequest = request.ParamsAs<QuantusLoginRequest>();

        if(string.IsNullOrEmpty(loginRequest?.Login))
            throw new StratumException(StratumError.MinusOne, "missing login");

        var split = loginRequest.Login.Split('.');
        context.Miner = split[0].Trim();
        context.Worker = split.Length > 1 ? split[1].Trim() : null;
        context.UserAgent = loginRequest.UserAgent?.Trim();

        var isValid = manager.ValidateAddress(context.Miner);
        context.IsAuthorized = isValid;
        context.IsSubscribed = isValid;

        if(!isValid)
        {
            await connection.RespondErrorAsync(StratumError.MinusOne, "invalid QTC address", request.Id, false);

            if(clusterConfig?.Banning?.BanOnLoginFailure is null or true)
            {
                logger.Info(() => $"[{connection.ConnectionId}] Banning unauthorized worker {context.Miner} for {loginFailureBanTimeout.TotalSeconds} sec");

                banManager.Ban(connection.RemoteEndpoint.Address, loginFailureBanTimeout);
                Disconnect(connection);
            }

            return;
        }

        context.ExtraNonce = manager.NextExtraNonce();

        // static difficulty from password: "d=<diff>"
        var staticDiff = GetStaticDiffFromPassparts(loginRequest.Password?.Split(PasswordControlVarsSeparator));

        var nicehashDiff = await GetNicehashStaticMinDiff(context, coin.Name, coin.GetAlgorithmName());

        if(nicehashDiff.HasValue && (!staticDiff.HasValue || nicehashDiff > staticDiff))
            staticDiff = nicehashDiff;

        if(staticDiff.HasValue &&
           (context.VarDiff != null && staticDiff.Value >= context.VarDiff.Config.MinDiff ||
               context.VarDiff == null && staticDiff.Value > context.Difficulty))
        {
            context.VarDiff = null;
            context.SetDifficulty(staticDiff.Value);

            logger.Info(() => $"[{connection.ConnectionId}] Static difficulty set to {staticDiff.Value}");
        }

        await connection.RespondAsync(new JsonRpcResponse<object>(new QuantusLoginResponse
        {
            Id = connection.ConnectionId,
            Job = CreateWorkerJob(connection),
        }, request.Id));

        logger.Info(() => $"[{connection.ConnectionId}] Authorized worker {context.Worker ?? "(default)"}@{context.Miner} [{context.UserAgent}]");
    }

    private async Task OnGetJobAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<QuantusWorkerContext>();

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        if(!context.IsAuthorized)
            throw new StratumException(StratumError.UnauthorizedWorker, "unauthorized");

        await connection.RespondAsync(new JsonRpcResponse<object>(CreateWorkerJob(connection), request.Id));
    }

    private async Task OnSubmitAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<QuantusWorkerContext>();

        try
        {
            if(request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            var requestAge = clock.Now - tsRequest.Timestamp.UtcDateTime;

            if(requestAge > maxShareAge)
            {
                logger.Warn(() => $"[{connection.ConnectionId}] Dropping stale share submission request (server overloaded?)");
                return;
            }

            context.LastActivity = clock.Now;

            if(!context.IsAuthorized)
                throw new StratumException(StratumError.UnauthorizedWorker, "unauthorized");

            var submitRequest = request.ParamsAs<QuantusSubmitRequest>();

            if(submitRequest == null)
                throw new StratumException(StratumError.MinusOne, "invalid params");

            var share = await manager.SubmitShareAsync(connection, submitRequest, ct);

            await connection.RespondAsync(new JsonRpcResponse<object>(new QuantusStatusResponse(), request.Id));

            messageBus.SendMessage(share);
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, true);

            logger.Info(() => $"[{connection.ConnectionId}] Share accepted: D={share.Difficulty}");

            if(share.IsBlockCandidate)
                poolStats.LastPoolBlockTime = clock.Now;

            context.Stats.ValidShares++;
            await UpdateVarDiffAsync(connection, false, ct);
        }

        catch(StratumException ex)
        {
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, false, (int) ex.Code);

            context.Stats.InvalidShares++;
            logger.Info(() => $"[{connection.ConnectionId}] Share rejected: {ex.Message} [{context.UserAgent}]");
            PublishRejectedShare(connection, context, ex.Message);

            ConsiderBan(connection, context, poolConfig.Banning);

            throw;
        }
    }

    private QuantusStratumJob CreateWorkerJob(StratumConnection connection)
    {
        var context = connection.ContextAs<QuantusWorkerContext>();
        var job = manager.GetJobForStratum();

        if(job == null)
            return null;

        var difficulty = (ulong) Math.Max(1, Math.Round(context.Difficulty));
        var workerJob = new QuantusWorkerJob($"{job.JobId}_{difficulty}", job, difficulty);

        lock(context)
        {
            context.AddJob(workerJob, manager.MaxActiveJobs);
        }

        return new QuantusStratumJob
        {
            Difficulty = difficulty,
            ExtraNonce = context.ExtraNonce,
            JobId = workerJob.Id,
            MiningHash = job.MiningHashHex,
            Target = QuantusJob.TargetToHex(QuantusJob.TargetFromDifficulty(difficulty)),
        };
    }

    private Task SendJobAsync(StratumConnection connection, bool cleanJobs)
    {
        var job = CreateWorkerJob(connection);

        if(job == null)
            return Task.CompletedTask;

        return connection.NotifyAsync(QuantusConstants.StratumJobNotify, new QuantusJobNotification
        {
            CleanJobs = cleanJobs,
            Job = job,
        });
    }

    private async Task OnNewJobAsync()
    {
        logger.Info(() => "Broadcasting job");

        await Guard(() => ForEachMinerAsync(async (connection, ct) =>
        {
            var context = connection.ContextAs<QuantusWorkerContext>();

            if(!context.IsAuthorized)
                return;

            context.ApplyPendingDifficulty();

            await SendJobAsync(connection, true);
        }));
    }

    #region Overrides

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<QuantusCoinTemplate>();

        base.Configure(pc, cc);
    }

    protected override async Task SetupJobManager(CancellationToken ct)
    {
        manager = ctx.Resolve<QuantusJobManager>(
            new TypedParameter(typeof(IExtraNonceProvider), new QuantusExtraNonceProvider(poolConfig.Id, clusterConfig.InstanceId)));

        manager.Configure(poolConfig, clusterConfig);

        await manager.StartAsync(ct);

        if(poolConfig.EnableInternalStratum == true)
        {
            disposables.Add(manager.Jobs
                .Select(_ => Observable.FromAsync(() =>
                    Guard(OnNewJobAsync, ex => logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}"))))
                .Concat()
                .Subscribe(_ => { }, ex => logger.Debug(ex, nameof(OnNewJobAsync))));

            // start with initial job
            await manager.Jobs.Take(1).ToTask(ct);
        }

        else
        {
            // keep updating NetworkStats
            disposables.Add(manager.Jobs.Subscribe());
        }
    }

    protected override async Task InitStatsAsync(CancellationToken ct)
    {
        await base.InitStatsAsync(ct);

        blockchainStats = manager.BlockchainStats;
    }

    protected override WorkerContextBase CreateWorkerContext()
    {
        return new QuantusWorkerContext();
    }

    protected override async Task OnRequestAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<QuantusWorkerContext>();

        try
        {
            switch(request.Method)
            {
                case QuantusConstants.StratumLogin:
                    await OnLoginAsync(connection, tsRequest);
                    break;

                case QuantusConstants.StratumGetJob:
                    await OnGetJobAsync(connection, tsRequest);
                    break;

                case QuantusConstants.StratumSubmit:
                    await OnSubmitAsync(connection, tsRequest, ct);
                    break;

                case QuantusConstants.StratumKeepAlive:
                    context.LastActivity = clock.Now;
                    await connection.RespondAsync(new JsonRpcResponse<object>(new QuantusStatusResponse("KEEPALIVED"), request.Id));
                    break;

                default:
                    logger.Debug(() => $"[{connection.ConnectionId}] Unsupported RPC request: {JsonConvert.SerializeObject(request, serializerSettings)}");

                    await connection.RespondErrorAsync(StratumError.Other, $"Unsupported request {request.Method}", request.Id);
                    break;
            }
        }

        catch(StratumException ex)
        {
            await connection.RespondErrorAsync(ex.Code, ex.Message, request.Id, false);
        }
    }

    /// <summary>
    /// Share difficulty is the expected number of hashes per share
    /// (target = (2^512-1) / difficulty), so hashrate = sum of difficulties / time.
    /// </summary>
    public override double HashrateFromShares(double shares, double interval)
    {
        return shares / interval;
    }

    public override double ShareMultiplier => 1;

    /// <summary>
    /// The new difficulty is only queued here and applied with the next job broadcast
    /// (quantus-node issues a new job every few seconds). Re-sending the current
    /// mining_hash under a new job id makes SRBMiner restart its nonce search and
    /// resubmit nonces it already sent, which the pool must reject as duplicates.
    /// </summary>
    protected override Task OnVarDiffUpdateAsync(StratumConnection connection, double newDiff, CancellationToken ct)
    {
        return base.OnVarDiffUpdateAsync(connection, newDiff, ct);
    }

    #endregion
}
