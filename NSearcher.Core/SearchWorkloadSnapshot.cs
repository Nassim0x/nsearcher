namespace NSearcher.Core;

internal readonly record struct SearchWorkloadSnapshot(
    SearchCandidate[] Candidates,
    int CandidateCount,
    long TotalBytes,
    long LargestFileBytes,
    long FilesWithKnownLength)
{
    public long AverageFileBytes => FilesWithKnownLength == 0 ? 0 : TotalBytes / FilesWithKnownLength;
}

internal readonly record struct SearchCandidate(string FullPath, string DisplayBasePath, long Length);
