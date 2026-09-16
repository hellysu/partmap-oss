namespace PartMap.Services;

public enum BatchSliceState
{
    Waiting,
    Running,
    Completed,
    Failed
}

public sealed record BatchSliceRequest(string SourcePath, string DisplayName, string GroupKey, int PlateId = 0, string PlateName = "");

public sealed record BatchSliceProgress(
    int Index,
    int Total,
    BatchSliceRequest Request,
    BatchSliceState State,
    string Message,
    string? TargetPath = null);

public sealed class BatchSlicingService
{
    private readonly Func<BatchSliceRequest, string, CancellationToken, Task<OneClickSliceResult>> _slice;

    public BatchSlicingService(OneClickSlicingService? service = null)
    {
        var actual = service ?? new OneClickSlicingService();
        _slice = (request, modelRoot, cancellationToken) =>
            actual.GenerateAsync(request.SourcePath, modelRoot, request.PlateId, request.PlateName, cancellationToken);
    }

    public BatchSlicingService(Func<string, string, CancellationToken, Task<OneClickSliceResult>> slice)
    {
        _slice = (request, modelRoot, cancellationToken) =>
            slice(request.SourcePath, modelRoot, cancellationToken);
    }

    public async Task<IReadOnlyList<BatchSliceProgress>> RunAsync(
        IReadOnlyList<BatchSliceRequest> requests,
        string modelRoot,
        IProgress<BatchSliceProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<BatchSliceProgress>(requests.Count);
        for (var index = 0; index < requests.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = requests[index];
            progress?.Report(new BatchSliceProgress(index + 1, requests.Count, request,
                BatchSliceState.Running, "正在切片…"));
            OneClickSliceResult sliced;
            try
            {
                sliced = await _slice(request, modelRoot, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                sliced = new OneClickSliceResult { Succeeded = false, Message = exception.Message };
            }

            var state = sliced.Succeeded ? BatchSliceState.Completed : BatchSliceState.Failed;
            var item = new BatchSliceProgress(index + 1, requests.Count, request, state,
                sliced.Message, sliced.TargetPath);
            results.Add(item);
            progress?.Report(item);
        }

        return results;
    }
}
