using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Net;
using System.Reactive.Linq;
using Autofac;
using AutoMapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Miningcore.Api.Extensions;
using Miningcore.Api.Responses;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Model.Projections;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Miningcore.Util;
using NLog;
using NLog.Targets;

namespace Miningcore.Api;

public static class ApiEndpoints
{
    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

    // Start of the effort window for pools that have not found a block yet
    // concurrent live share streams (Server-Sent Events) across all pools
    private static int activeShareStreams;
    private const int MaxShareStreams = 500;

    private static readonly DateTime EffortEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Bitcoin-family shares are stored as stratumDifficulty / ShareMultiplier and the network
    // difficulty comes from the block target, so both are already in the same units.
    // Multiplying by ShareMultiplier (65536 for scrypt) inflated LTC/DOGE effort 65536x.
    private static double EffortShareConst(IMiningPool pool) =>
        pool is Blockchain.Bitcoin.BitcoinPool ? 1d : pool.ShareMultiplier;

    public static void MapMiningcoreApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/help", (EndpointDataSource endpointDataSource) =>
        {
            var endpoints = endpointDataSource.Endpoints.OfType<RouteEndpoint>();
            var tmp = endpoints.Select(e =>
            {
                var method = e.Metadata.OfType<HttpMethodMetadata>().FirstOrDefault()?.HttpMethods.FirstOrDefault() ?? "GET";
                method = $"{method,-5}";
                return $"{method} -> {e.RoutePattern.RawText}";
            });

            var result = string.Join("\n", tmp).Replace("{", "<").Replace("}", ">") + "\n";
            return Results.Text(result);
        });

        api.MapGet("/health-check", () => Results.Text("👍"));

        api.MapGet("/meta", () => Results.Json(new
        {
            generator = "Miningcore Community Edition",
            repo = "soosho/miningcore"
        }));

        MapClusterApi(api);
        MapPoolApi(api);
        MapAdminApi(api);
    }

    private static void MapClusterApi(RouteGroupBuilder api)
    {
        api.MapGet("/blocks", async (
            [FromServices] IConnectionFactory cf,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15,
            [FromQuery] BlockStatus[] state = null) =>
        {
            var ct = httpContext.RequestAborted;
            var blockStates = state is { Length: > 0 } ?
                state :
                new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

            var enabledPools = new HashSet<string>(clusterConfig.Pools.Where(x => x.Enabled).Select(x => x.Id));

            var blocks = (await cf.Run(con => blocksRepo.PageBlocksAsync(con, blockStates, page, pageSize == 0 ? 15 : pageSize, ct)))
                .Select(mapper.Map<Responses.Block>)
                .Where(x => enabledPools.Contains(x.PoolId))
                .ToArray();

            var blocksByPool = blocks.GroupBy(x => x.PoolId);

            foreach (var poolBlocks in blocksByPool)
            {
                var pool = GetPoolNoThrow(clusterConfig, poolBlocks.Key);

                if (pool == null)
                    continue;

                var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

                if (blockInfobaseDict != null)
                {
                    foreach (var block in poolBlocks)
                    {
                        blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                        if (!string.IsNullOrEmpty(blockInfobaseUrl))
                        {
                            if (blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                                block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                            else if (blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                                block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                        }
                    }
                }
            }

            return blocks;
        });
    }

    private static void MapAdminApi(RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin");

        admin.MapGet("/logging/level/{level}", (string level) =>
        {
            if (string.IsNullOrEmpty(level))
                throw new ApiException("Invalid logging level", HttpStatusCode.BadRequest);

            var logLevel = LogLevel.FromString(level);

            if (logLevel == null)
                throw new ApiException("Invalid logging level", HttpStatusCode.BadRequest);

            logger.Error("Admin update Logging Level this is Error");
            logger.Trace("Admin update Logging Level this is Trace");

            foreach (var rule in LogManager.Configuration.LoggingRules)
            {
                rule.EnableLoggingForLevel(logLevel);
                rule.SetLoggingLevels(logLevel, LogLevel.Fatal);
            }

            Target target = LogManager.Configuration.FindTargetByName("console");

            if (target != null)
            {
                var loggingConfig = LogManager.Configuration;
                loggingConfig.AddRule(logLevel, LogLevel.Fatal, target);
                LogManager.Configuration = loggingConfig;
            }

            LogManager.ReconfigExistingLoggers();

            logger.Error("Admin update Logging Level this is Error AFTER");
            logger.Trace("Admin update Logging Level this is Trace AFTER");

            logger.Info($"Logging level set to {level}");
            return "Ok";
        });

        admin.MapGet("/payment/processing/enable", (
            [FromServices] ConcurrentDictionary<string, IMiningPool> pools) =>
        {
            var poolIdsUpdated = new List<string>();
            foreach (var pool in pools.Values)
            {
                if (!pool.Config.Enabled) continue;

                poolIdsUpdated.Add(pool.Config.Id);
                pool.Config.PaymentProcessing.Enabled = true;
            }

            var poolIdsCsv = string.Join(",", poolIdsUpdated);
            logger.Info(() => $"Enabled payment processing for pool {poolIdsCsv}");

            return poolIdsCsv;
        });

        admin.MapGet("/payment/processing/disable", (
            [FromServices] ConcurrentDictionary<string, IMiningPool> pools) =>
        {
            var poolIdsUpdated = new List<string>();
            foreach (var pool in pools.Values)
            {
                if (!pool.Config.Enabled) continue;

                poolIdsUpdated.Add(pool.Config.Id);
                pool.Config.PaymentProcessing.Enabled = false;
            }

            var poolIdsCsv = string.Join(",", poolIdsUpdated);
            logger.Info(() => $"Disabled payment processing for pool {poolIdsCsv}");

            return poolIdsCsv;
        });

        admin.MapGet("/payment/processing/{poolId}/enable", (
            string poolId,
            [FromServices] ConcurrentDictionary<string, IMiningPool> pools) =>
        {
            if (string.IsNullOrEmpty(poolId))
                throw new ApiException("Missing pool ID", HttpStatusCode.BadRequest);

            pools.TryGetValue(poolId, out var poolInstance);
            if (poolInstance == null)
                return "-1";

            poolInstance.Config.PaymentProcessing.Enabled = true;
            logger.Info(() => $"Enabled payment processing for pool {poolId}");
            return "Ok";
        });

        admin.MapGet("/payment/processing/{poolId}/disable", (
            string poolId,
            [FromServices] ConcurrentDictionary<string, IMiningPool> pools) =>
        {
            if (string.IsNullOrEmpty(poolId))
                throw new ApiException("Missing pool ID", HttpStatusCode.BadRequest);

            pools.TryGetValue(poolId, out var poolInstance);
            if (poolInstance == null)
                return "-1";

            poolInstance.Config.PaymentProcessing.Enabled = false;
            logger.Info(() => $"Disabled payment processing for pool {poolId}");
            return "Ok";
        });

        admin.MapGet("/stats/gc", (
            [FromServices] Responses.AdminGcStats gcStats) =>
        {
            gcStats.GcGen0 = GC.CollectionCount(0);
            gcStats.GcGen1 = GC.CollectionCount(1);
            gcStats.GcGen2 = GC.CollectionCount(2);
            gcStats.MemAllocated = FormatUtil.FormatCapacity(GC.GetTotalMemory(false));

            return gcStats;
        });

        admin.MapPost("/forcegc", () =>
        {
            GC.Collect(2, GCCollectionMode.Forced);
            return "Ok";
        });

        admin.MapGet("/pools/{poolId}/miners/{address}/getbalance", async (
            string poolId, string address,
            [FromServices] IConnectionFactory cf,
            [FromServices] IBalanceRepository balanceRepo) =>
        {
            return await cf.Run(con => balanceRepo.GetBalanceAsync(con, poolId, address));
        });

        admin.MapGet("/pools/{poolId}/miners/{address}/settings", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IMinerRepository minerRepo,
            [FromServices] IMapper mapper) =>
        {
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            var result = await cf.Run(con => minerRepo.GetSettingsAsync(con, null, pool.Id, address));

            if (result == null)
                throw new ApiException("No settings found", HttpStatusCode.NotFound);

            return mapper.Map<Responses.MinerSettings>(result);
        });

        admin.MapPost("/pools/{poolId}/miners/{address}/settings", async (
            string poolId, string address,
            [FromBody] Responses.MinerSettings settings,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IMinerRepository minerRepo,
            [FromServices] IMapper mapper) =>
        {
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (settings == null)
                throw new ApiException("Invalid or missing settings", HttpStatusCode.BadRequest);

            var mapped = mapper.Map<Persistence.Model.MinerSettings>(settings);

            if (pool.PaymentProcessing != null)
                mapped.PaymentThreshold = Math.Max(mapped.PaymentThreshold, pool.PaymentProcessing.MinimumPayment);

            mapped.PoolId = pool.Id;
            mapped.Address = address;

            var result = await cf.RunTx(async (con, tx) =>
            {
                await minerRepo.UpdateSettingsAsync(con, tx, mapped);
                return await minerRepo.GetSettingsAsync(con, tx, mapped.PoolId, mapped.Address);
            });

            logger.Info(() => $"Updated settings for pool {pool.Id}, miner {address}");

            return mapper.Map<Responses.MinerSettings>(result);
        });
    }

    private static void MapPoolApi(RouteGroupBuilder api)
    {
        var poolsGroup = api.MapGroup("/pools");
        var poolsGroupV2 = api.MapGroup("/v2/pools");

        poolsGroup.MapGet("/", async (
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IStatsRepository statsRepo,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] IShareRepository shareRepo,
            [FromServices] IMasterClock clock,
            [FromServices] IMapper mapper,
            [FromServices] ConcurrentDictionary<string, IMiningPool> pools,
            HttpContext httpContext,
            [FromQuery] uint topMinersRange = 24) =>
        {
            var ct = httpContext.RequestAborted;
            topMinersRange = topMinersRange == 0 ? 24 : topMinersRange;

            var response = new GetPoolsResponse
            {
                Pools = await Task.WhenAll(clusterConfig.Pools.Where(x => x.Enabled).Select(async config =>
                {
                    try
                    {
                    var stats = await cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, config.Id, ct));
                    pools.TryGetValue(config.Id, out var pool);
                    var result = config.ToPoolInfo(mapper, stats, pool, clusterConfig.Proxied);

                    result.TotalPaid = await cf.Run(con => statsRepo.GetTotalPoolPaymentsAsync(con, config.Id, ct));
                    result.TotalBlocks = await cf.Run(con => blocksRepo.GetPoolBlockCountAsync(con, config.Id, ct));
                    result.TotalConfirmedBlocks = await cf.Run(con => blocksRepo.GetTotalConfirmedBlocksAsync(con, config.Id, ct));
                    result.TotalPendingBlocks = await cf.Run(con => blocksRepo.GetTotalPendingBlocksAsync(con, config.Id, ct));
                    result.BlockReward = await cf.Run(con => blocksRepo.GetLastConfirmedBlockRewardAsync(con, config.Id, ct));
                    var lastBlockTime = await cf.Run(con => blocksRepo.GetLastPoolBlockTimeAsync(con, config.Id, ct));
                    result.LastPoolBlockTime = lastBlockTime;

                    var payoutConfig = config.PaymentProcessing;
                    if(result.PaymentProcessing != null)
                    {
                        result.PaymentProcessing.PayoutSchemeConfig = payoutConfig?.PayoutSchemeConfig?.ToObject<ApiPoolPayoutSchemeConfig>();
                        if(payoutConfig?.PayoutScheme != PayoutScheme.PPLNSBF && result.PaymentProcessing.PayoutSchemeConfig != null)
                            result.PaymentProcessing.PayoutSchemeConfig.BlockFinderPercentage = null;
                    }

                    if (pool != null)
                    {
                        // before the pool's first block, effort counts from the first share
                        var startTime = lastBlockTime ?? EffortEpoch;
                        var poolEffort = await cf.Run(con => shareRepo.GetEffortBetweenCreatedAsync(con, config.Id, EffortShareConst(pool), startTime, clock.Now, ct));
                        if (poolEffort.HasValue)
                            result.PoolEffort = poolEffort.Value;
                    }

                    var from = clock.Now.AddHours(-topMinersRange);
                    var minersByHashrate = await cf.Run(con => statsRepo.PagePoolMinersByHashrateAsync(con, config.Id, from, 0, 15, ct));

                    result.TopMiners = minersByHashrate.Select(mapper.Map<MinerPerformanceStats>).ToArray();

                    return result;
                    }
                    catch(Exception ex)
                    {
                        logger.Error(() => $"Error fetching data for pool {config.Id}: {ex.Message}");
                        return null;
                    }
                }).Where(x => x != null).ToArray())
            };

            return response;
        });

        poolsGroup.MapGet("/{poolId}", async (
            string poolId,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IStatsRepository statsRepo,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] IShareRepository shareRepo,
            [FromServices] IMasterClock clock,
            [FromServices] IMapper mapper,
            [FromServices] ConcurrentDictionary<string, IMiningPool> poolsDict,
            HttpContext httpContext,
            [FromQuery] uint topMinersRange = 24) =>
        {
            var ct = httpContext.RequestAborted;
            topMinersRange = topMinersRange == 0 ? 24 : topMinersRange;
            var pool = GetPool(clusterConfig, poolId);

            var stats = await cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, pool.Id, ct));
            poolsDict.TryGetValue(pool.Id, out var poolInstance);

            var response = new GetPoolResponse
            {
                Pool = pool.ToPoolInfo(mapper, stats, poolInstance, clusterConfig.Proxied)
            };

            response.Pool.TotalPaid = await cf.Run(con => statsRepo.GetTotalPoolPaymentsAsync(con, pool.Id, ct));
            response.Pool.TotalBlocks = await cf.Run(con => blocksRepo.GetPoolBlockCountAsync(con, pool.Id, ct));
            response.Pool.TotalConfirmedBlocks = await cf.Run(con => blocksRepo.GetTotalConfirmedBlocksAsync(con, pool.Id, ct));
            response.Pool.TotalPendingBlocks = await cf.Run(con => blocksRepo.GetTotalPendingBlocksAsync(con, pool.Id, ct));
            response.Pool.BlockReward = await cf.Run(con => blocksRepo.GetLastConfirmedBlockRewardAsync(con, pool.Id, ct));
            var lastBlockTime = await cf.Run(con => blocksRepo.GetLastPoolBlockTimeAsync(con, pool.Id, ct));
            response.Pool.LastPoolBlockTime = lastBlockTime;

            var payoutConfig = pool.PaymentProcessing;
            if(response.Pool.PaymentProcessing != null)
            {
                response.Pool.PaymentProcessing.PayoutSchemeConfig = payoutConfig?.PayoutSchemeConfig?.ToObject<ApiPoolPayoutSchemeConfig>();
                if(payoutConfig?.PayoutScheme != PayoutScheme.PPLNSBF && response.Pool.PaymentProcessing.PayoutSchemeConfig != null)
                    response.Pool.PaymentProcessing.PayoutSchemeConfig.BlockFinderPercentage = null;
            }

            if (poolInstance != null)
            {
                // before the pool's first block, effort counts from the first share
                var startTime = lastBlockTime ?? EffortEpoch;
                var poolEffort = await cf.Run(con => shareRepo.GetEffortBetweenCreatedAsync(con, pool.Id, EffortShareConst(poolInstance), startTime, clock.Now, ct));
                if (poolEffort.HasValue)
                    response.Pool.PoolEffort = poolEffort.Value;
            }

            var from = clock.Now.AddHours(-topMinersRange);

            response.Pool.TopMiners = (await cf.Run(con => statsRepo.PagePoolMinersByHashrateAsync(con, pool.Id, from, 0, 15, ct)))
                .Select(mapper.Map<MinerPerformanceStats>)
                .ToArray();

            return response;
        });

        poolsGroup.MapGet("/{poolId}/performance", async (
            string poolId,
            [FromQuery(Name = "r")] SampleRange? range,
            [FromQuery(Name = "i")] SampleInterval? interval,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IStatsRepository statsRepo,
            [FromServices] IMasterClock clock,
            [FromServices] IMapper mapper,
            HttpContext httpContext) =>
        {
            var ct = httpContext.RequestAborted;
            range ??= SampleRange.Day;
            interval ??= SampleInterval.Hour;
            var pool = GetPool(clusterConfig, poolId);

            var end = clock.Now;
            DateTime start;

            switch (range.Value)
            {
                case SampleRange.Day:
                    start = end.AddDays(-1);
                    break;

                case SampleRange.Month:
                    start = end.AddDays(-30);
                    break;

                default:
                    throw new ApiException("invalid interval");
            }

            var stats = await cf.Run(con => statsRepo.GetPoolPerformanceBetweenAsync(con, pool.Id, interval.Value, start, end, ct));

            var response = new GetPoolStatsResponse
            {
                Stats = stats.Select(mapper.Map<AggregatedPoolStats>).ToArray()
            };

            return response;
        });

        poolsGroup.MapGet("/{poolId}/miners", async (
            string poolId,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IStatsRepository statsRepo,
            [FromServices] IMasterClock clock,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15,
            [FromQuery] uint topMinersRange = 24) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            topMinersRange = topMinersRange == 0 ? 24 : topMinersRange;
            var pool = GetPool(clusterConfig, poolId);

            var end = clock.Now;
            var start = end.AddHours(-topMinersRange);

            var miners = (await cf.Run(con => statsRepo.PagePoolMinersByHashrateAsync(con, pool.Id, start, page, pageSize, ct)))
                .Select(mapper.Map<MinerPerformanceStats>)
                .ToArray();

            return miners;
        });

        poolsGroup.MapGet("/{poolId}/blocks", async (
            string poolId,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15,
            [FromQuery] BlockStatus[] state = null) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            var blockStates = state is { Length: > 0 } ?
                state :
                new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

            var blocks = (await cf.Run(con => blocksRepo.PageBlocksAsync(con, pool.Id, blockStates, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Block>)
                .ToArray();

            var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

            foreach (var block in blocks)
            {
                if (blockInfobaseDict != null)
                {
                    blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                    if (!string.IsNullOrEmpty(blockInfobaseUrl))
                    {
                        if (blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                        else if (blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                    }
                }
            }

            return blocks;
        });

        poolsGroupV2.MapGet("/{poolId}/blocks", async (
            string poolId,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15,
            [FromQuery] BlockStatus[] state = null) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            var blockStates = state is { Length: > 0 } ?
                state :
                new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

            uint itemCount = await cf.Run(con => blocksRepo.GetPoolBlockCountAsync(con, poolId, ct));
            uint pageCount = (uint)Math.Floor(itemCount / (double)pageSize);

            var blocks = (await cf.Run(con => blocksRepo.PageBlocksAsync(con, pool.Id, blockStates, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Block>)
                .ToArray();

            var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

            foreach (var block in blocks)
            {
                if (blockInfobaseDict != null)
                {
                    blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                    if (!string.IsNullOrEmpty(blockInfobaseUrl))
                    {
                        if (blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                        else if (blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                    }
                }
            }

            return new PagedResultResponse<Responses.Block[]>(blocks, itemCount, pageCount);
        });

        poolsGroup.MapGet("/{poolId}/payments", async (
            string poolId,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                    con, pool.Id, null, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Payment>)
                .ToArray();

            var txInfobaseUrl = pool.Template.ExplorerTxLink;
            var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

            foreach (var payment in payments)
            {
                if (!string.IsNullOrEmpty(txInfobaseUrl))
                    payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

                if (!string.IsNullOrEmpty(addressInfobaseUrl))
                    payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
            }

            return payments;
        });

        poolsGroupV2.MapGet("/{poolId}/payments", async (
            string poolId,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            uint itemCount = await cf.Run(con => paymentsRepo.GetPaymentsCountAsync(con, poolId, null, ct));
            uint pageCount = (uint)Math.Floor(itemCount / (double)pageSize);

            var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                    con, pool.Id, null, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Payment>)
                .ToArray();

            var txInfobaseUrl = pool.Template.ExplorerTxLink;
            var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

            foreach (var payment in payments)
            {
                if (!string.IsNullOrEmpty(txInfobaseUrl))
                    payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

                if (!string.IsNullOrEmpty(addressInfobaseUrl))
                    payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
            }

            return new PagedResultResponse<Responses.Payment[]>(payments, itemCount, pageCount);
        });

        poolsGroup.MapGet("/{poolId}/miners/{address}", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IStatsRepository statsRepo,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] IShareRepository shareRepo,
            [FromServices] IMasterClock clock,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] SampleRange? perfMode = null) =>
        {
            var ct = httpContext.RequestAborted;
            perfMode ??= SampleRange.Day;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var statsResult = await cf.RunTx((con, tx) =>
                statsRepo.GetMinerStatsAsync(con, tx, pool.Id, address, ct), true, IsolationLevel.Serializable);

            Responses.MinerStats stats = null;

            if (statsResult != null)
            {
                stats = mapper.Map<Responses.MinerStats>(statsResult);

                if (pool.Template.Family == CoinFamily.Bitcoin)
                    stats.PendingShares *= pool.Template.As<BitcoinTemplate>().ShareMultiplier;

                if (statsResult.LastPayment != null)
                {
                    stats.LastPayment = statsResult.LastPayment.Created;
                    var baseUrl = pool.Template.ExplorerTxLink;
                    if (!string.IsNullOrEmpty(baseUrl))
                        stats.LastPaymentLink = string.Format(baseUrl, statsResult.LastPayment.TransactionConfirmationData);
                }

                var lastBlockTime = await cf.Run(con => blocksRepo.GetLastPoolBlockTimeAsync(con, pool.Id, ct));
                {
                    // before the pool's first block, effort counts from the first share
                    var startTime = lastBlockTime ?? EffortEpoch;
                    var minerEffort = await cf.Run(con => shareRepo.GetMinerEffortBetweenCreatedAsync(con, pool.Id, address, startTime, clock.Now, ct));

                    // shares are stored as difficulty / ShareMultiplier, which is already in
                    // network-difficulty units, so no scaling is needed here
                    if (minerEffort.HasValue)
                        stats.MinerEffort = minerEffort.Value;
                }

                stats.PerformanceSamples = await GetMinerPerformanceInternal(cf, statsRepo, clock, mapper, perfMode.Value, pool, address, ct);

                stats.TotalConfirmedBlocks = await cf.Run(con => statsRepo.GetMinerTotalConfirmedBlocksAsync(con, pool.Id, address, ct));
                stats.TotalPendingBlocks = await cf.Run(con => statsRepo.GetMinerTotalPendingBlocksAsync(con, pool.Id, address, ct));
            }

            return stats;
        });

        // Live share stream (Server-Sent Events): pushes this miner's accepted and rejected
        // shares as they happen. Filtered server-side so a client only receives its own shares.
        poolsGroup.MapGet("/{poolId}/miners/{address}/sharestream", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] ConcurrentDictionary<string, IMiningPool> pools,
            [FromServices] Messaging.IMessageBus messageBus,
            HttpContext httpContext) =>
        {
            var ct = httpContext.RequestAborted;
            var pool = GetPool(clusterConfig, poolId);

            if(string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if(pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            if(Interlocked.Increment(ref activeShareStreams) > MaxShareStreams)
            {
                Interlocked.Decrement(ref activeShareStreams);
                httpContext.Response.StatusCode = (int) HttpStatusCode.ServiceUnavailable;
                return;
            }

            try
            {
                var multiplier = pools.TryGetValue(pool.Id, out var miningPool) ? miningPool.ShareMultiplier : 1d;

                var channel = System.Threading.Channels.Channel.CreateBounded<string>(
                    new System.Threading.Channels.BoundedChannelOptions(256)
                    {
                        FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                        SingleReader = true
                    });

                static string Json(object o) => Newtonsoft.Json.JsonConvert.SerializeObject(o);

                using var accepted = messageBus.Listen<Share>()
                    .Where(x => x.PoolId == pool.Id && x.Miner == address)
                    .Subscribe(x => channel.Writer.TryWrite(Json(new
                    {
                        type = "accepted",
                        worker = x.Worker ?? "",
                        difficulty = x.Difficulty * multiplier,
                        blockCandidate = x.IsBlockCandidate,
                        blockHeight = x.IsBlockCandidate ? x.BlockHeight : (long?) null,
                        created = x.Created
                    })));

                using var rejected = messageBus.Listen<Notifications.Messages.ShareRejectedNotification>()
                    .Where(x => x.PoolId == pool.Id && x.Miner == address)
                    .Subscribe(x => channel.Writer.TryWrite(Json(new
                    {
                        type = "rejected",
                        worker = x.Worker ?? "",
                        difficulty = x.Difficulty,
                        reason = x.Reason,
                        created = x.Created
                    })));

                var response = httpContext.Response;
                response.Headers.ContentType = "text/event-stream";
                response.Headers.CacheControl = "no-cache";
                response.Headers["X-Accel-Buffering"] = "no";   // nginx: don't buffer the stream

                await response.WriteAsync(": connected\n\n", ct);
                await response.Body.FlushAsync(ct);

                while(!ct.IsCancellationRequested)
                {
                    // wait for the next share, sending a keep-alive comment every 15s
                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    waitCts.CancelAfter(TimeSpan.FromSeconds(15));

                    try
                    {
                        await channel.Reader.WaitToReadAsync(waitCts.Token);
                    }

                    catch(OperationCanceledException) when(!ct.IsCancellationRequested)
                    {
                        await response.WriteAsync(": ping\n\n", ct);
                        await response.Body.FlushAsync(ct);
                        continue;
                    }

                    while(channel.Reader.TryRead(out var item))
                        await response.WriteAsync($"data: {item}\n\n", ct);

                    await response.Body.FlushAsync(ct);
                }
            }

            catch(OperationCanceledException)
            {
                // client went away
            }

            finally
            {
                Interlocked.Decrement(ref activeShareStreams);
            }
        });

        poolsGroup.MapGet("/{poolId}/miners/{address}/blocks", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15,
            [FromQuery] BlockStatus[] state = null) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var blockStates = state is { Length: > 0 } ?
                state :
                new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

            var blocks = (await cf.Run(con => blocksRepo.PageMinerBlocksAsync(con, pool.Id, address, blockStates, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Block>)
                .ToArray();

            var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

            foreach (var block in blocks)
            {
                if (blockInfobaseDict != null)
                {
                    blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                    if (!string.IsNullOrEmpty(blockInfobaseUrl))
                    {
                        if (blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                        else if (blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                    }
                }
            }

            return blocks;
        });

        poolsGroupV2.MapGet("/{poolId}/miners/{address}/blocks", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IBlockRepository blocksRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15,
            [FromQuery] BlockStatus[] state = null) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var blockStates = state is { Length: > 0 } ?
                state :
                new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

            uint itemCount = await cf.Run(con => blocksRepo.GetMinerBlockCountAsync(con, poolId, address, ct));
            uint pageCount = (uint)Math.Floor(itemCount / (double)pageSize);

            var blocks = (await cf.Run(con => blocksRepo.PageMinerBlocksAsync(con, pool.Id, address, blockStates, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Block>)
                .ToArray();

            var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

            foreach (var block in blocks)
            {
                if (blockInfobaseDict != null)
                {
                    blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                    if (!string.IsNullOrEmpty(blockInfobaseUrl))
                    {
                        if (blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                        else if (blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                            block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                    }
                }
            }

            return new PagedResultResponse<Responses.Block[]>(blocks, itemCount, pageCount);
        });

        poolsGroup.MapGet("/{poolId}/miners/{address}/payments", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                    con, pool.Id, address, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Payment>)
                .ToArray();

            var txInfobaseUrl = pool.Template.ExplorerTxLink;
            var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

            foreach (var payment in payments)
            {
                if (!string.IsNullOrEmpty(txInfobaseUrl))
                    payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

                if (!string.IsNullOrEmpty(addressInfobaseUrl))
                    payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
            }

            return payments;
        });

        poolsGroupV2.MapGet("/{poolId}/miners/{address}/payments", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            uint itemCount = await cf.Run(con => paymentsRepo.GetPaymentsCountAsync(con, poolId, address, ct));
            uint pageCount = (uint)Math.Floor(itemCount / (double)pageSize);

            var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                    con, pool.Id, address, page, pageSize, ct)))
                .Select(mapper.Map<Responses.Payment>)
                .ToArray();

            var txInfobaseUrl = pool.Template.ExplorerTxLink;
            var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

            foreach (var payment in payments)
            {
                if (!string.IsNullOrEmpty(txInfobaseUrl))
                    payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

                if (!string.IsNullOrEmpty(addressInfobaseUrl))
                    payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
            }

            return new PagedResultResponse<Responses.Payment[]>(payments, itemCount, pageCount);
        });

        poolsGroup.MapGet("/{poolId}/miners/{address}/balancechanges", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var balanceChanges = (await cf.Run(con => paymentsRepo.PageBalanceChangesAsync(
                    con, pool.Id, address, page, pageSize, ct)))
                .Select(mapper.Map<Responses.BalanceChange>)
                .ToArray();

            return balanceChanges;
        });

        poolsGroupV2.MapGet("/{poolId}/miners/{address}/balancechanges", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            uint itemCount = await cf.Run(con => paymentsRepo.GetBalanceChangesCountAsync(con, poolId, address));
            uint pageCount = (uint)Math.Floor(itemCount / (double)pageSize);

            var balanceChanges = (await cf.Run(con => paymentsRepo.PageBalanceChangesAsync(
                    con, pool.Id, address, page, pageSize, ct)))
                .Select(mapper.Map<Responses.BalanceChange>)
                .ToArray();

            return new PagedResultResponse<Responses.BalanceChange[]>(balanceChanges, itemCount, pageCount);
        });

        poolsGroup.MapGet("/{poolId}/miners/{address}/earnings/daily", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var earnings = (await cf.Run(con => paymentsRepo.PageMinerPaymentsByDayAsync(
                    con, pool.Id, address, page, pageSize, ct)))
                .ToArray();

            return earnings;
        });

        poolsGroupV2.MapGet("/{poolId}/miners/{address}/earnings/daily", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IPaymentRepository paymentsRepo,
            HttpContext httpContext,
            [FromQuery] int page = 0,
            [FromQuery] int pageSize = 15) =>
        {
            var ct = httpContext.RequestAborted;
            pageSize = pageSize == 0 ? 15 : pageSize;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            uint itemCount = await cf.Run(con => paymentsRepo.GetMinerPaymentsByDayCountAsync(con, poolId, address));
            uint pageCount = (uint)Math.Floor(itemCount / (double)pageSize);

            var earnings = (await cf.Run(con => paymentsRepo.PageMinerPaymentsByDayAsync(
                    con, pool.Id, address, page, pageSize, ct)))
                .ToArray();

            return new PagedResultResponse<AmountByDate[]>(earnings, itemCount, pageCount);
        });

        poolsGroup.MapGet("/{poolId}/miners/{address}/performance", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IStatsRepository statsRepo,
            [FromServices] IMasterClock clock,
            [FromServices] IMapper mapper,
            HttpContext httpContext,
            [FromQuery] SampleRange? mode = null) =>
        {
            var ct = httpContext.RequestAborted;
            mode ??= SampleRange.Day;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var result = await GetMinerPerformanceInternal(cf, statsRepo, clock, mapper, mode.Value, pool, address, ct);
            return result;
        });

        poolsGroup.MapGet("/{poolId}/miners/{address}/settings", async (
            string poolId, string address,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IMinerRepository minerRepo,
            [FromServices] IMapper mapper) =>
        {
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            var result = await cf.Run(con => minerRepo.GetSettingsAsync(con, null, pool.Id, address));

            if (result == null)
                throw new ApiException("No settings found", HttpStatusCode.NotFound);

            return mapper.Map<Responses.MinerSettings>(result);
        });

        poolsGroup.MapPost("/{poolId}/miners/{address}/settings", async (
            string poolId, string address,
            [FromBody] Requests.UpdateMinerSettingsRequest request,
            [FromServices] ClusterConfig clusterConfig,
            [FromServices] IConnectionFactory cf,
            [FromServices] IShareRepository shareRepo,
            [FromServices] IMinerRepository minerRepo,
            [FromServices] IMapper mapper,
            HttpContext httpContext) =>
        {
            var ct = httpContext.RequestAborted;
            var pool = GetPool(clusterConfig, poolId);

            if (string.IsNullOrEmpty(address))
                throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

            if (pool.Template.Family == CoinFamily.Ethereum)
                address = address.ToLower();

            if (request?.Settings == null)
                throw new ApiException("Invalid or missing settings", HttpStatusCode.BadRequest);

            if (!IPAddress.TryParse(request.IpAddress, out var requestIp))
                throw new ApiException("Invalid IP address", HttpStatusCode.BadRequest);

            var ips = await cf.Run(con => shareRepo.GetRecentyUsedIpAddressesAsync(con, null, poolId, address, ct));

            if (ips == null || ips.Length == 0)
                throw new ApiException("Address not recently used for mining", HttpStatusCode.NotFound);

            if (!ips.Any(x => IPAddress.TryParse(x, out var ipAddress) && ipAddress.IsEqual(requestIp)))
                throw new ApiException("None of the recently used IP addresses matches the request", HttpStatusCode.Forbidden);

            var mapped = mapper.Map<Persistence.Model.MinerSettings>(request.Settings);

            if (pool.PaymentProcessing != null)
                mapped.PaymentThreshold = Math.Max(mapped.PaymentThreshold, pool.PaymentProcessing.MinimumPayment);

            mapped.PoolId = pool.Id;
            mapped.Address = address;

            return await cf.RunTx(async (con, tx) =>
            {
                await minerRepo.UpdateSettingsAsync(con, tx, mapped);
                logger.Info(() => $"Updated settings for pool {pool.Id}, miner {address}");
                var result = await minerRepo.GetSettingsAsync(con, tx, mapped.PoolId, mapped.Address);
                return mapper.Map<Responses.MinerSettings>(result);
            });
        });
    }

    private static async Task<Responses.WorkerPerformanceStatsContainer[]> GetMinerPerformanceInternal(
        IConnectionFactory cf, IStatsRepository statsRepo, IMasterClock clock, IMapper mapper,
        SampleRange mode, PoolConfig pool, string address, CancellationToken ct)
    {
        Persistence.Model.Projections.WorkerPerformanceStatsContainer[] stats = null;
        var end = clock.Now;
        DateTime start;

        switch (mode)
        {
            case SampleRange.Hour:
                end = end.AddSeconds(-end.Second);
                start = end.AddHours(-1);
                stats = await cf.Run(con => statsRepo.GetMinerPerformanceBetweenThreeMinutelyAsync(con, pool.Id, address, start, end, ct));
                break;

            case SampleRange.Day:
                if (end.Minute < 30) end = end.AddHours(-1);
                end = end.AddMinutes(-end.Minute);
                end = end.AddSeconds(-end.Second);
                start = end.AddDays(-1);
                stats = await cf.Run(con => statsRepo.GetMinerPerformanceBetweenHourlyAsync(con, pool.Id, address, start, end, ct));
                break;

            case SampleRange.Month:
                if (end.Hour < 12) end = end.AddDays(-1);
                end = end.Date;
                start = end.AddMonths(-1);
                stats = await cf.Run(con => statsRepo.GetMinerPerformanceBetweenDailyAsync(con, pool.Id, address, start, end, ct));
                break;
        }

        var result = mapper.Map<Responses.WorkerPerformanceStatsContainer[]>(stats);
        return result;
    }

    private static PoolConfig GetPoolNoThrow(ClusterConfig clusterConfig, string poolId)
    {
        if (string.IsNullOrEmpty(poolId))
            return null;

        var pool = clusterConfig.Pools.FirstOrDefault(x => x.Id == poolId && x.Enabled);
        return pool;
    }

    private static PoolConfig GetPool(ClusterConfig clusterConfig, string poolId)
    {
        if (string.IsNullOrEmpty(poolId))
            throw new ApiException("Invalid pool id", HttpStatusCode.NotFound);

        var pool = clusterConfig.Pools.FirstOrDefault(x => x.Id == poolId && x.Enabled);

        if (pool == null)
            throw new ApiException($"Unknown pool {poolId}", HttpStatusCode.NotFound);

        return pool;
    }
}
