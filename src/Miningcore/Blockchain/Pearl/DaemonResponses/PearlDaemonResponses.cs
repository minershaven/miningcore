using Newtonsoft.Json;

namespace Miningcore.Blockchain.Pearl.DaemonResponses;

// ---------------------------------------------------------------------------
// getblockchaininfo
// (node/btcjson GetBlockChainInfoResult)
// ---------------------------------------------------------------------------
public class PearlBlockchainInfo
{
    [JsonProperty("chain")]
    public string Chain { get; set; }

    [JsonProperty("blocks")]
    public ulong Blocks { get; set; }

    [JsonProperty("headers")]
    public ulong Headers { get; set; }

    [JsonProperty("bestblockhash")]
    public string BestBlockHash { get; set; }

    [JsonProperty("difficulty")]
    public double Difficulty { get; set; }

    [JsonProperty("verificationprogress")]
    public double VerificationProgress { get; set; }

    [JsonProperty("pruned")]
    public bool Pruned { get; set; }
}

// ---------------------------------------------------------------------------
// getblocktemplate
// (node/btcjson GetBlockTemplateResult — only the fields the pool needs)
// ---------------------------------------------------------------------------
public class PearlBlockTemplateTx
{
    [JsonProperty("data")]
    public string Data { get; set; }

    [JsonProperty("hash")]
    public string Hash { get; set; }

    [JsonProperty("txid")]
    public string TxId { get; set; }

    [JsonProperty("depends")]
    public int[] Depends { get; set; }

    [JsonProperty("fee")]
    public long Fee { get; set; }

    [JsonProperty("vsize")]
    public long VSize { get; set; }
}

public class PearlCoinbaseAux
{
    [JsonProperty("flags")]
    public string Flags { get; set; }
}

public class PearlBlockTemplate
{
    [JsonProperty("bits")]
    public string Bits { get; set; }

    [JsonProperty("curtime")]
    public long CurTime { get; set; }

    [JsonProperty("height")]
    public ulong Height { get; set; }

    [JsonProperty("previousblockhash")]
    public string PreviousBlockHash { get; set; }

    [JsonProperty("vsizelimit")]
    public long VSizeLimit { get; set; }

    [JsonProperty("transactions")]
    public PearlBlockTemplateTx[] Transactions { get; set; }

    [JsonProperty("version")]
    public int Version { get; set; }

    [JsonProperty("longpollid")]
    public string LongPollId { get; set; }

    [JsonProperty("target")]
    public string Target { get; set; }

    [JsonProperty("maxtime")]
    public long MaxTime { get; set; }

    [JsonProperty("mintime")]
    public long MinTime { get; set; }

    [JsonProperty("mutable")]
    public string[] Mutable { get; set; }

    [JsonProperty("noncerange")]
    public string NonceRange { get; set; }

    [JsonProperty("capabilities")]
    public string[] Capabilities { get; set; }

    [JsonProperty("coinbaseaux")]
    public PearlCoinbaseAux CoinbaseAux { get; set; }

    [JsonProperty("coinbasevalue")]
    public long CoinbaseValue { get; set; }

    [JsonProperty("default_witness_commitment")]
    public string DefaultWitnessCommitment { get; set; }

    /// <summary>
    /// Any other getblocktemplate fields (e.g. "requiredcertversion"), kept so
    /// the template round-trips unchanged to the Python bridge.
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, Newtonsoft.Json.Linq.JToken> ExtraFields { get; set; }
}

// ---------------------------------------------------------------------------
// getpeerinfo
// ---------------------------------------------------------------------------
public class PearlPeerInfo
{
    [JsonProperty("addr")]
    public string Addr { get; set; }

    [JsonProperty("inbound")]
    public bool Inbound { get; set; }
}

// ---------------------------------------------------------------------------
// validateaddress
// ---------------------------------------------------------------------------
public class PearlValidateAddressResponse
{
    [JsonProperty("isvalid")]
    public bool IsValid { get; set; }

    [JsonProperty("address")]
    public string Address { get; set; }
}

// ---------------------------------------------------------------------------
// getblock (verbose) — used for block confirmation in payout handler
// ---------------------------------------------------------------------------
public class PearlGetBlockResponse
{
    [JsonProperty("hash")]
    public string Hash { get; set; }

    [JsonProperty("confirmations")]
    public long Confirmations { get; set; }

    [JsonProperty("height")]
    public ulong Height { get; set; }
}
