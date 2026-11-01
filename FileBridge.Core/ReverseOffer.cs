namespace FileBridge.Core;

/// <summary>Explicitly offered source files, scoped to one cancellable reverse-send operation.</summary>
/// <param name="Id">Opaque offer identifier; it is not a filesystem path.</param>
/// <param name="Files">Safe relative names, sizes and whole-file SHA-256 values.</param>
/// <param name="ParallelChunks">Maximum concurrent download requests supported by this offer.</param>
public sealed record ReverseOffer(string Id, FileManifest[] Files, int ParallelChunks);
