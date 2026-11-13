namespace Miningcore.Notifications.Messages;

/// <summary>
/// Published on the message bus whenever a pool rejects a share, so that
/// live consumers (e.g. the miner share stream API) can show bad shares as they happen.
/// Accepted shares are already published as <see cref="Miningcore.Blockchain.Share"/>.
/// </summary>
public record ShareRejectedNotification(
    string PoolId,
    string Miner,
    string Worker,
    double Difficulty,
    string Reason,
    DateTime Created);
