using System.Globalization;
using System.Numerics;
using Miningcore.Blockchain.Pearl.DaemonResponses;
using Miningcore.Contracts;
using Miningcore.Stratum;
using Miningcore.Time;
using NLog;

namespace Miningcore.Blockchain.Pearl;

/// <summary>
/// One Pearl mining job, built from a getblocktemplate result.
///
/// The work unit handed to miners is a "MiningJob": the incomplete block-header
/// bytes plus the 256-bit target. Miners run the matrix proof-of-useful-work and
/// return a serialized PlainProof. On submit, the Python bridge:
///   1. generate_proof(incomplete_header, plain_proof) -> ZKProof
///   2. assembles PearlBlock:
///        ZKCertificate.serialize() | PearlHeader.serialize() | varint(txs) | txns
///   3. returns the block hex ready for submitblock.
///
/// The bridge also reports whether the solution met the network target so the
/// pool can classify the share as a block candidate.
/// </summary>
public class PearlJob
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private PearlBlockTemplate _template;
    private PearlPythonBridge  _bridge;
    private string             _miningAddress;
    private BigInteger         _networkTarget;

    public string JobId             { get; private set; } = string.Empty;
    public ulong  BlockHeight       { get; private set; }
    public string PreviousBlockHash { get; private set; } = string.Empty;
    public PearlBlockTemplate Template => _template;

    public void Init(
        PearlBlockTemplate template,
        string             jobId,
        IMasterClock       clock,
        string             miningAddress,
        PearlPythonBridge  bridge)
    {
        Contract.RequiresNonNull(template);
        Contract.RequiresNonNull(bridge);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId));

        _template      = template;
        _bridge        = bridge;
        _miningAddress = miningAddress;
        JobId          = jobId;
        BlockHeight    = template.Height;
        PreviousBlockHash = template.PreviousBlockHash;

        _networkTarget = ParseTarget(template.Target);

        // Fallback: derive the target from the compact 'bits' field when the
        // GBT result carries no explicit target.
        if(_networkTarget <= BigInteger.Zero && !string.IsNullOrEmpty(template.Bits))
            _networkTarget = BitsToTarget(template.Bits);

        if(_networkTarget > BigInteger.Zero)
            NetworkDifficulty = (double) PearlConstants.Diff1Target / (double) _networkTarget;
    }

    /// <summary>Network difficulty derived from the job target (diff-1 ratio).</summary>
    public double NetworkDifficulty { get; private set; }

    private static BigInteger ParseTarget(string hex)
    {
        if(string.IsNullOrEmpty(hex))
            return BigInteger.Zero;

        return BigInteger.Parse("0" + hex.TrimStart('0'), NumberStyles.HexNumber);
    }

    /// <summary>Convert a compact-format 'bits' hex string to a 256-bit target.</summary>
    private static BigInteger BitsToTarget(string bitsHex)
    {
        var bits     = uint.Parse(bitsHex, NumberStyles.HexNumber);
        var exponent = (int) (bits >> 24);
        var mantissa = new BigInteger(bits & 0x007fffff);

        return exponent <= 3
            ? mantissa >> (8 * (3 - exponent))
            : mantissa << (8 * (exponent - 3));
    }

    /// <summary>
    /// Convert a stratum difficulty to a 256-bit share-target hex string
    /// (64 lowercase hex chars): target = Diff1Target / difficulty.
    /// </summary>
    private static string DifficultyToTargetHex(double difficulty)
    {
        if(difficulty <= 0)
            difficulty = 1;

        // Scale to preserve fractional difficulties.
        const long scale = 1L << 16;
        var quotient = (PearlConstants.Diff1Target * scale) / new BigInteger(difficulty * scale);

        if(quotient > PearlConstants.MaxTarget)
            quotient = PearlConstants.MaxTarget;

        var hex = quotient.ToString("x");
        if(hex.Length > 64)
            hex = hex[^64..];

        return hex.PadLeft(64, '0');
    }

    /// <summary>
    /// Stratum mining.notify params for Pearl:
    ///   [jobId, incompleteHeaderBytesHex, target, height, cleanJobs]
    ///
    /// The miner deserializes the incomplete header, runs the PoUW against the
    /// target, and submits a serialized PlainProof.
    /// </summary>
    /// <summary>
    /// mining.notify params for Pearl (lpminer expects an OBJECT, not an array):
    ///   {
    ///     "job_id":     "...",
    ///     "height":     <ulong>,
    ///     "header":     "<incomplete header hex>",
    ///     "target":     "<share target hex, 64 chars>",
    ///     "difficulty": <worker stratum difficulty, nonzero>,
    ///     "clean_jobs": <bool>
    ///   }
    ///
    /// difficulty is the per-worker stratum difficulty (from the port config /
    /// vardiff), and target is the matching share target derived from it
    /// (Diff1Target / difficulty). The pool-side block check uses the network
    /// target independently.
    /// </summary>
    public object GetJobParams(bool cleanJobs, double workerDifficulty = 1.0)
    {
        // The bridge produces the canonical incomplete-header bytes for this
        // template (built from version/prevhash/merkleroot/time/bits and the
        // coinbase for the pool's mining address), cached by jobId.
        var headerHex = _bridge.GetIncompleteHeaderHex(JobId);

        if(workerDifficulty <= 0)
            workerDifficulty = 1.0;

        return new Dictionary<string, object>
        {
            // Pearl stratum v1 notify fields (header = 76-byte incomplete header)
            ["header"]     = headerHex,
            ["height"]     = _template.Height,
            ["job_id"]     = JobId,
            ["target"]     = DifficultyToTargetHex(workerDifficulty),
        };
    }

    /// <summary>
    /// Validates a miner's PlainProof and, if it meets target, produces the
    /// final block hex via the Python bridge.
    /// Returns (Share, blockHex?). blockHex is non-null only when IsBlockCandidate.
    /// </summary>
    public async Task<(Share share, string blockHex)> ProcessShareAsync(
        StratumConnection worker,
        string            plainProofBase64,
        PearlPythonBridge bridge,
        IMasterClock      clock,
        CancellationToken ct)
    {
        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(bridge);

        var context = worker.ContextAs<PearlWorkerContext>();

        // A VarDiff retarget can race with a share already in flight. Verify
        // against the easier of the current and previous difficulty for a
        // short grace window and credit the share at that difficulty.
        var stratumDiff = context.Difficulty;
        if(context.PreviousDifficulty.HasValue && context.PreviousDifficulty.Value < stratumDiff &&
           context.VarDiff?.LastUpdate != null &&
           clock.Now - context.VarDiff.LastUpdate.Value < TimeSpan.FromSeconds(30))
            stratumDiff = context.PreviousDifficulty.Value;

        PearlProofResult result;
        try
        {
            // The bridge verifies the PlainProof against the share target for
            // stratumDiff and, if it also clears the network target, builds the
            // ZK proof and the final block hex.
            result = await bridge.SubmitPlainProofAsync(JobId, plainProofBase64, stratumDiff, ct);
        }
        catch(Exception ex)
        {
            Logger.Warn(ex, "Pearl bridge SubmitPlainProof failed");
            throw new StratumException(StratumError.Other, PearlStratumErrors.ProofVerifyFailed);
        }

        if(!result.Valid)
        {
            var err = string.IsNullOrEmpty(result.Error) ? PearlStratumErrors.ProofVerifyFailed : result.Error;
            var code = err.Contains("target", StringComparison.OrdinalIgnoreCase) || err.Contains("jackpot", StringComparison.OrdinalIgnoreCase)
                ? StratumError.LowDifficultyShare
                : StratumError.Other;
            throw new StratumException(code, err);
        }

        var share = new Share
        {
            BlockHeight       = (long) BlockHeight,
            NetworkDifficulty = this.NetworkDifficulty > 0 ? this.NetworkDifficulty : result.NetworkDifficulty,
            Difficulty        = stratumDiff,
            BlockReward       = _template.CoinbaseValue / PearlConstants.SmallestUnit,
        };

        if(result.MeetsNetworkTarget && !string.IsNullOrEmpty(result.BlockHex))
        {
            share.IsBlockCandidate = true;
            share.BlockHash        = result.BlockHash;
            return (share, result.BlockHex);
        }

        return (share, null);
    }
}
