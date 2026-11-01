namespace FileBridge.Core;

/// <summary>Local progress stages; these values are not part of the HTTP protocol.</summary>
public enum TransferStage
{
    Pending,
    CalculatingHash,
    VerifyingResume,
    WaitingForData,
    Receiving,
    VerifyingFile,
    ReceiveComplete,
    ResendingCorrupt,
    Sending,
    SendComplete,
    AlreadyComplete,
    Retrying,
    ReceiveError,
    WaitingForPeer,
    WaitingForSender
}

/// <summary>Local UI progress for one file; byte counts include verified resumed data.</summary>
public sealed record TransferProgress(
    string FileName,
    TransferStage Stage,
    long Completed = 0,
    long Total = 0,
    double BytesPerSecond = 0,
    int ParallelChunks = 1,
    string? Error = null,
    int RetryAttempt = 0,
    int RetryLimit = 0);
