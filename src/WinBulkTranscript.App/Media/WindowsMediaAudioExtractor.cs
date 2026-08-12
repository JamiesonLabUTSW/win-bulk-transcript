using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using WinBulkTranscript.Core.Domain;
using WinBulkTranscript.Core.Ports;
using WinBulkTranscript.Core.Wave;

namespace WinBulkTranscript.App.Media;

internal enum MediaExtractionLifecycleCheckpoint
{
    Prepare,
    Transcode,
    Validation,
}

internal interface IWindowsMediaAudioExtractorTestLifecycleObserver
{
    Task OnCheckpointAsync(MediaExtractionLifecycleCheckpoint checkpoint, CancellationToken cancellationToken);
}

/// <summary>Uses Windows MediaTranscoder to create a validated temporary PCM16 WAV file.</summary>
public sealed class WindowsMediaAudioExtractor : IAudioExtractor
{
    private const string TemporaryDirectoryName = "WinBulkTranscript";
    private const string TemporaryWaveFilePrefix = "wbt-pcm-";
    private const string TemporaryWaveFileExtension = ".wav";
    private const string TemporaryInputFilePrefix = "wbt-input-";
    private const string TemporaryInputFileExtension = ".mp4";
    private static readonly TimeSpan StaleFileAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan CancellationCleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly IWindowsMediaAudioExtractorTestLifecycleObserver? testLifecycleObserver;

    public WindowsMediaAudioExtractor()
        : this(null)
    {
    }

    internal WindowsMediaAudioExtractor(IWindowsMediaAudioExtractorTestLifecycleObserver? testLifecycleObserver)
    {
        this.testLifecycleObserver = testLifecycleObserver;

    }

    public async Task<TemporaryPcmWave> ExtractAsync(string inputPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("The input MP4 file no longer exists.", inputPath);
        }

        var temporaryDirectoryPath = GetTemporaryDirectory();
        Directory.CreateDirectory(temporaryDirectoryPath);
        string? temporaryPath = null;
        string? stagedInputPath = null;
        var cleanupOwnedByTranscode = false;
        StorageFile? inputFile = null;
        try
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            if (RequiresInputStaging(fullInputPath))
            {
                try
                {
                    stagedInputPath = await StageInputForStorageAsync(
                        fullInputPath,
                        temporaryDirectoryPath,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (IOException exception)
                {
                    throw new MediaExtractionException(
                        $"Windows could not stage long input '{Path.GetFileName(inputPath)}' for media transcoding: {exception.Message}",
                        exception);
                }
                catch (UnauthorizedAccessException exception)
                {
                    throw new MediaExtractionException(
                        $"Windows could not stage long input '{Path.GetFileName(inputPath)}' for media transcoding: {exception.Message}",
                        exception);
                }
            }

            inputFile = await StorageFile.GetFileFromPathAsync(stagedInputPath ?? fullInputPath).AsTask(cancellationToken).ConfigureAwait(false);
            var temporaryDirectory = await StorageFolder.GetFolderFromPathAsync(temporaryDirectoryPath).AsTask(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var outputFile = await temporaryDirectory
                .CreateFileAsync(CreateOwnedTemporaryWaveFileName(), CreationCollisionOption.FailIfExists)
                .AsTask()
                .ConfigureAwait(false);
            var ownedTemporaryPath = outputFile.Path;
            if (!IsOwnedTemporaryWavePath(ownedTemporaryPath))
            {
                throw new InvalidOperationException("Windows media returned a temporary WAV path outside the extractor-owned location.");
            }

            temporaryPath = ownedTemporaryPath;
            cancellationToken.ThrowIfCancellationRequested();

            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
            profile.Audio = AudioEncodingProperties.CreatePcm(16_000, 1, 16);
            var transcoder = new MediaTranscoder();
            await ReachTestLifecycleCheckpointAsync(MediaExtractionLifecycleCheckpoint.Prepare, cancellationToken).ConfigureAwait(false);
            var prepared = await transcoder.PrepareFileTranscodeAsync(
                inputFile ?? throw new InvalidOperationException("Windows media did not return an input file for transcoding."),
                outputFile,
                profile).AsTask(cancellationToken).ConfigureAwait(false);
            if (!prepared.CanTranscode)
            {
                throw await CreatePreparationFailureAsync(inputFile, inputPath, prepared.FailureReason).ConfigureAwait(false);
            }

            await ReachTestLifecycleCheckpointAsync(MediaExtractionLifecycleCheckpoint.Transcode, cancellationToken).ConfigureAwait(false);
            var operation = prepared.TranscodeAsync();
            operation.Progress += (_, fraction) => progress?.Report(Math.Clamp(fraction / 100d, 0, 1));
            cleanupOwnedByTranscode = await AwaitTranscodeAsync(
                operation,
                operation.AsTask(),
                ownedTemporaryPath,
                stagedInputPath,
                cancellationToken).ConfigureAwait(false);
            if (cleanupOwnedByTranscode)
            {
                // Native completion owns both artifacts after cancellation.
                stagedInputPath = null;
            }
            else if (stagedInputPath is not null)
            {
                await DeleteTemporaryFileAsync(stagedInputPath).ConfigureAwait(false);
                stagedInputPath = null;
            }

            await ReachTestLifecycleCheckpointAsync(MediaExtractionLifecycleCheckpoint.Validation, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            PcmWaveFile waveFile;
            try
            {
                waveFile = WaveFileReader.Open(ownedTemporaryPath);
            }
            catch (WaveFileFormatException exception)
            {
                throw new MediaExtractionException($"Windows transcoding did not produce PCM16, 16 kHz, mono audio: {exception.Message}", exception);
            }

            progress?.Report(1);
            return new TemporaryPcmWave(waveFile, () => DeleteTemporaryFileAsync(ownedTemporaryPath));
        }
        catch (Exception exception)
        {
            if (!cleanupOwnedByTranscode && temporaryPath is not null)
            {
                await DeleteTemporaryFileAsync(temporaryPath).ConfigureAwait(false);
            }

            if (!cleanupOwnedByTranscode && stagedInputPath is not null)
            {
                await DeleteTemporaryFileAsync(stagedInputPath).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "Windows media extraction was cancelled.",
                    exception,
                    cancellationToken);
            }

            if (exception is MediaExtractionException)
            {
                throw;
            }

            throw await CreateUnexpectedMediaFailureAsync(inputFile, inputPath, exception).ConfigureAwait(false);
        }
    }

    private static bool RequiresInputStaging(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        // StorageFile.GetFileFromPathAsync rejects valid filesystem paths at the legacy WinRT
        // boundary once they reach MAX_PATH. Stage only those inputs under our short temp root;
        // normal paths stay zero-copy and the source file is never altered.
        return fullPath.Length >= 260;
    }

    private static async Task<string> StageInputForStorageAsync(
        string sourcePath,
        string temporaryDirectoryPath,
        CancellationToken cancellationToken)
    {
        var stagedPath = Path.Combine(temporaryDirectoryPath, CreateOwnedTemporaryInputFileName());
        try
        {
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                stagedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await source.CopyToAsync(destination, 128 * 1024, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            return stagedPath;
        }
        catch
        {
            await DeleteTemporaryFileAsync(stagedPath).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReachTestLifecycleCheckpointAsync(
        MediaExtractionLifecycleCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var observer = testLifecycleObserver;
        if (observer is null)
        {
            return;
        }

        await observer.OnCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task<MediaExtractionException> CreatePreparationFailureAsync(
        StorageFile? inputFile,
        string inputPath,
        object? nativeFailureReason)
    {
        var diagnosis = await DiagnoseInputAsync(inputFile, inputPath).ConfigureAwait(false);
        var nativeReasonText = nativeFailureReason?.ToString();
        var nativeReasonSuffix = string.IsNullOrWhiteSpace(nativeReasonText)
            ? string.Empty
            : $" Windows transcode reason: {nativeReasonText}.";
        return new MediaExtractionException(
            $"Windows could not prepare '{Path.GetFileName(inputPath)}' for PCM audio. {diagnosis}{nativeReasonSuffix}");
    }

    private static async Task<MediaExtractionException> CreateUnexpectedMediaFailureAsync(
        StorageFile? inputFile,
        string inputPath,
        Exception exception)
    {
        var diagnosis = await DiagnoseInputAsync(inputFile, inputPath).ConfigureAwait(false);
        return new MediaExtractionException(
            $"Windows could not read or transcode '{Path.GetFileName(inputPath)}' to PCM audio. {diagnosis}",
            exception);
    }

    private static async Task<string> DiagnoseInputAsync(StorageFile? inputFile, string inputPath)
    {
        // StorageFile can reject a container before it becomes available to MediaSource. Inspect
        // the original file first so the deliberately zero-reference audio fixture is still
        // diagnosed as empty instead of being reported as an opaque native failure.
        if (!string.IsNullOrWhiteSpace(inputPath)
            && await HasExplicitZeroSampleTableAsync(inputPath).ConfigureAwait(false))
        {
            return "The input's audio sample table declares zero samples and zero decoded duration; it contains no usable audio samples.";
        }

        if (inputFile is null)
        {
            return "Windows could not inspect the input container; it is unreadable or corrupt, or its audio codec is unsupported.";
        }

        if (await HasExplicitZeroSampleTableAsync(inputFile.Path).ConfigureAwait(false))
        {
            return "The input's audio sample table declares zero samples and zero decoded duration; it contains no usable audio samples.";
        }

        try
        {
            var profile = await MediaEncodingProfile.CreateFromFileAsync(inputFile);
            var audioTrackCount = profile.GetAudioTracks().Count;
            var videoTrackCount = profile.GetVideoTracks().Count;
            if (audioTrackCount == 0)
            {
                return $"Windows found no usable audio track (audio tracks: 0; video tracks: {videoTrackCount}).";
            }

            var audioSubtype = profile.Audio?.Subtype;
            if (string.IsNullOrWhiteSpace(audioSubtype)
                || string.Equals(audioSubtype, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return "Windows found an audio track, but no supported audio codec was identified.";
            }

            try
            {
                using var source = MediaSource.CreateFromStorageFile(inputFile);
                await source.OpenAsync();
                if (source.Duration is not { } duration || duration <= TimeSpan.Zero)
                {
                    return "Windows found an audio track with zero decoded duration and no usable audio samples.";
                }
            }
            catch (Exception)
            {
                return $"Windows found audio subtype '{audioSubtype}', but it could not be opened for decoding; the media is unreadable or corrupt, or the audio codec is unsupported.";
            }

            return $"Windows found audio subtype '{audioSubtype}', but it could not be transcoded; the audio codec or media stream is unsupported or corrupt.";
        }
        catch (Exception)
        {
            return "Windows could not inspect the input container; it is unreadable or corrupt, or its audio codec is unsupported.";
        }
    }

    private static async Task<bool> HasExplicitZeroSampleTableAsync(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 20 || info.Length > 4 * 1024 * 1024)
            {
                return false;
            }

            var bytes = await File.ReadAllBytesAsync(path, CancellationToken.None).ConfigureAwait(false);
            var hasZeroTimeToSampleEntries = false;
            var hasZeroSampleCount = false;
            for (var offset = 0; offset <= bytes.Length - 20; offset++)
            {
                if (!TryGetIsoBoxEnd(bytes, offset, out var boxEnd))
                {
                    continue;
                }

                var type = bytes.AsSpan(offset + 4, 4);
                if (type.SequenceEqual("stts"u8)
                    && offset + 16 <= boxEnd
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 12, 4)) == 0)
                {
                    hasZeroTimeToSampleEntries = true;
                }
                else if ((type.SequenceEqual("stsz"u8) || type.SequenceEqual("stz2"u8))
                    && offset + 20 <= boxEnd
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 16, 4)) == 0)
                {
                    hasZeroSampleCount = true;
                }

                if (hasZeroTimeToSampleEntries && hasZeroSampleCount)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryGetIsoBoxEnd(byte[] bytes, int offset, out int boxEnd)
    {
        boxEnd = 0;
        if (offset < 0 || offset > bytes.Length - 8)
        {
            return false;
        }

        var boxSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
        var headerSize = 8;
        if (boxSize == 1)
        {
            if (offset > bytes.Length - 16)
            {
                return false;
            }

            var extendedSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset + 8, 8));
            if (extendedSize > int.MaxValue)
            {
                return false;
            }

            boxSize = (uint)extendedSize;
            headerSize = 16;
        }
        else if (boxSize == 0)
        {
            boxSize = (uint)(bytes.Length - offset);
        }

        if (boxSize < headerSize || boxSize > bytes.Length - offset)
        {
            return false;
        }

        boxEnd = checked(offset + (int)boxSize);
        return true;
    }

    private static async Task<bool> AwaitTranscodeAsync(
        Windows.Foundation.IAsyncInfo operation,
        Task transcodeTask,
        string temporaryPath,
        string? stagedInputPath,
        CancellationToken cancellationToken)
    {
        using var cancellationRegistration = cancellationToken.Register(
            static state => CancelBestEffort((Windows.Foundation.IAsyncInfo)state!),
            operation);
        try
        {
            await transcodeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelBestEffort(operation);
            var cleanupTask = CompleteTranscodeAndDeleteAsync(transcodeTask, temporaryPath, stagedInputPath);
            try
            {
                await cleanupTask.WaitAsync(CancellationCleanupTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The operation still owns the file. Keep deletion tied to its eventual completion
                // instead of racing a live media transcode during window-close cancellation.
                // CompleteTranscodeAndDeleteAsync observes the native operation and owns deletion.
            }

            return true;
        }
    }

    private static void CancelBestEffort(Windows.Foundation.IAsyncInfo operation)
    {
        try
        {
            operation.Cancel();
        }
        catch (Exception)
        {
            // Cancellation is cooperative; cleanup still waits for the operation to settle.
        }
    }

    private static async Task CompleteTranscodeAndDeleteAsync(
        Task transcodeTask,
        string temporaryPath,
        string? stagedInputPath)
    {
        try
        {
            await transcodeTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A cancelled or failed transcode still must release its owned temporary file.
        }

        try
        {
            await DeleteTemporaryFileAsync(temporaryPath).ConfigureAwait(false);
            if (stagedInputPath is not null)
            {
                await DeleteTemporaryFileAsync(stagedInputPath).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Stale cleanup will retry a rare unexpected delete failure on the next batch.
        }
    }

    /// <summary>Best-effort startup maintenance; stale files never alter final VTT output.</summary>
    public static void CleanupStaleTemporaryFiles()
    {
        var directory = GetTemporaryDirectory();
        if (!Directory.Exists(directory))
        {
            return;
        }

        var threshold = DateTimeOffset.UtcNow - StaleFileAge;
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.*", SearchOption.TopDirectoryOnly))
            {
                if (!IsOwnedTemporaryWavePath(file.FullName)
                    && !IsOwnedTemporaryInputPath(file.FullName))
                {
                    continue;
                }

                try
                {
                    if (file.LastWriteTimeUtc <= threshold)
                    {
                        file.Delete();
                    }
                }
                catch (Exception) when (file.Exists)
                {
                    // Best effort only: an in-use stale file can be retried on the next start.
                }
            }
        }
        catch (IOException)
        {
            // Lack of cleanup access or a concurrently removed directory must not prevent a new batch.
        }
        catch (UnauthorizedAccessException)
        {
            // Lack of cleanup permission must not prevent a new batch from starting.
        }
    }

    /// <summary>
    /// Gets whether a path is an exact temporary WAV name created by this extractor.
    /// This lets integration evidence distinguish extractor-owned files from unrelated user files.
    /// </summary>
    public static bool IsOwnedTemporaryWavePath(string path)
        => IsOwnedTemporaryPath(path, TemporaryWaveFilePrefix, TemporaryWaveFileExtension);

    /// <summary>Gets whether a path is a short-path copy staged for a long media input.</summary>
    public static bool IsOwnedTemporaryInputPath(string path)
        => IsOwnedTemporaryPath(path, TemporaryInputFilePrefix, TemporaryInputFileExtension);

    private static bool IsOwnedTemporaryPath(string path, string prefix, string extension)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.Equals(directory, GetTemporaryDirectory(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var fileName = Path.GetFileName(fullPath);
            if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
                || !fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var identifierLength = fileName.Length - prefix.Length - extension.Length;
            return identifierLength == 32
                && Guid.TryParseExact(fileName.Substring(prefix.Length, identifierLength), "N", out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string GetTemporaryDirectory()
        => Path.Combine(Path.GetTempPath(), TemporaryDirectoryName);

    private static string CreateOwnedTemporaryWaveFileName()
        => $"{TemporaryWaveFilePrefix}{Guid.NewGuid():N}{TemporaryWaveFileExtension}";

    private static string CreateOwnedTemporaryInputFileName()
        => $"{TemporaryInputFilePrefix}{Guid.NewGuid():N}{TemporaryInputFileExtension}";

    private static ValueTask DeleteTemporaryFileAsync(string temporaryPath)
    {
        if (!IsOwnedTemporaryWavePath(temporaryPath)
            && !IsOwnedTemporaryInputPath(temporaryPath))
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (IOException)
        {
            // Nothing under the final output name is affected; stale cleanup handles a retry.
        }
        catch (UnauthorizedAccessException)
        {
            // See comment above.
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class MediaExtractionException : Exception
{
    public MediaExtractionException(string message)
        : base(message)
    {
    }

    public MediaExtractionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
