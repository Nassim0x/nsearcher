namespace NSearcher.Core;

internal static class SearchRuntimeTuner
{
    private const long DefaultAvailableMemoryBytes = 2L * 1024 * 1024 * 1024;
    private const int MinGeneralStreamBufferSize = 128 * 1024;
    private const int MaxGeneralStreamBufferSize = 1024 * 1024;
    private const int MinFastLiteralStreamBufferSize = 128 * 1024;
    private const int MaxFastLiteralStreamBufferSize = 4 * 1024 * 1024;
    private const long TinyCorpusBytes = 4L * 1024 * 1024;
    private const long SmallCorpusBytes = 32L * 1024 * 1024;
    private const long ModerateFastLiteralCorpusBytes = 128L * 1024 * 1024;
    private const long MediumFileBytes = 128L * 1024;
    private const long LargeFileBytes = 1024L * 1024;
    private const long HugeFileBytes = 16L * 1024 * 1024;

    public static SearchRuntimeProfile CreateProfile(
        SearchOptions options,
        SearchWorkloadSnapshot workload,
        bool canUseFastLiteralFileMode)
    {
        var logicalProcessors = Math.Max(1, Environment.ProcessorCount);
        var availableMemory = GetAvailableMemoryBytes();
        var autoTuned = options.MaxDegreeOfParallelism == 0;

        var workerCount = autoTuned
            ? ResolveWorkerCount(logicalProcessors, availableMemory, workload, canUseFastLiteralFileMode)
            : options.MaxDegreeOfParallelism;

        var streamBufferSize = ResolveStreamBufferSize(workerCount, availableMemory, workload, canUseFastLiteralFileMode);

        return new SearchRuntimeProfile(
            WorkerCount: workerCount,
            StreamBufferSize: streamBufferSize,
            TotalAvailableMemoryBytes: availableMemory,
            AutoTuned: autoTuned,
            UsedFastLiteralFileMode: canUseFastLiteralFileMode);
    }

    private static int ResolveWorkerCount(
        int logicalProcessors,
        long availableMemory,
        SearchWorkloadSnapshot workload,
        bool canUseFastLiteralFileMode)
    {
        if (workload.CandidateCount <= 1)
        {
            return 1;
        }

        var maxUsefulWorkers = canUseFastLiteralFileMode
            ? logicalProcessors * 2
            : logicalProcessors;

        var memoryBound = (int)Math.Clamp(availableMemory / (32L * 1024 * 1024), 1, 256);
        var averageFileBytes = workload.AverageFileBytes;
        var bytesPerWorkerTarget = canUseFastLiteralFileMode
            ? 64L * 1024 * 1024
            : 48L * 1024 * 1024;
        var workerCountFromBytes = (int)Math.Clamp(
            (workload.TotalBytes + bytesPerWorkerTarget - 1) / bytesPerWorkerTarget,
            1,
            256);
        var filesPerWorkerSlice = averageFileBytes switch
        {
            >= HugeFileBytes => 1,
            >= LargeFileBytes => 4,
            >= MediumFileBytes => 16,
            _ => 64
        };
        var workerCountFromFiles = Math.Max(1, (workload.CandidateCount + filesPerWorkerSlice - 1) / filesPerWorkerSlice);

        var desired = Math.Max(workerCountFromBytes, workerCountFromFiles);

        if (workload.TotalBytes <= TinyCorpusBytes)
        {
            desired = Math.Min(desired, 2);
        }
        else if (workload.TotalBytes <= SmallCorpusBytes)
        {
            desired = Math.Min(desired, logicalProcessors);
        }
        else if (workload.TotalBytes <= ModerateFastLiteralCorpusBytes &&
                 canUseFastLiteralFileMode &&
                 averageFileBytes >= LargeFileBytes)
        {
            desired = Math.Min(desired, Math.Max(2, (logicalProcessors * 2) / 3));
        }

        if (canUseFastLiteralFileMode &&
            averageFileBytes >= LargeFileBytes &&
            workload.TotalBytes > ModerateFastLiteralCorpusBytes)
        {
            desired = Math.Max(desired, logicalProcessors);
        }

        return Math.Clamp(desired, 1, Math.Min(memoryBound, maxUsefulWorkers));
    }

    private static int ResolveStreamBufferSize(
        int workerCount,
        long availableMemory,
        SearchWorkloadSnapshot workload,
        bool canUseFastLiteralFileMode)
    {
        var minBufferSize = canUseFastLiteralFileMode
            ? MinFastLiteralStreamBufferSize
            : MinGeneralStreamBufferSize;
        var maxBufferSize = canUseFastLiteralFileMode
            ? MaxFastLiteralStreamBufferSize
            : MaxGeneralStreamBufferSize;
        var averageFileBytes = workload.AverageFileBytes;
        var targetBufferSize = canUseFastLiteralFileMode
            ? averageFileBytes switch
            {
                >= 64L * 1024 * 1024 => 4 * 1024 * 1024,
                >= 8L * 1024 * 1024 => 2 * 1024 * 1024,
                >= LargeFileBytes => 1024 * 1024,
                _ => 256 * 1024
            }
            : averageFileBytes switch
            {
                >= HugeFileBytes => 1024 * 1024,
                >= LargeFileBytes => 512 * 1024,
                _ => 128 * 1024
            };

        var perWorkerBudget = (int)Math.Clamp(
            availableMemory / Math.Max(workerCount, 1) / 32,
            minBufferSize,
            maxBufferSize);

        return Math.Clamp(targetBufferSize, minBufferSize, Math.Min(maxBufferSize, perWorkerBudget));
    }

    private static long GetAvailableMemoryBytes()
    {
        try
        {
            var memoryInfo = GC.GetGCMemoryInfo();
            if (memoryInfo.TotalAvailableMemoryBytes > 0)
            {
                return memoryInfo.TotalAvailableMemoryBytes;
            }
        }
        catch (Exception)
        {
        }

        return DefaultAvailableMemoryBytes;
    }
}
