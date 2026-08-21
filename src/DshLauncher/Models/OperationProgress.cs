namespace DshLauncher.Models;

internal sealed record OperationProgress(
    string Message,
    int? Percentage = null,
    string? Detail = null);
