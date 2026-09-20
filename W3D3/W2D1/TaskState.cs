namespace W2D1;

internal enum TaskStage { Planning, Execution, Validation, Done }
internal enum TaskExecutionStatus { Active, Paused }

// Независимое от диалога состояние. Изменения создают копию до записи на диск.
internal sealed record TaskState
{
    public string TaskDescription { get; init; } = "";
    public TaskStage Stage { get; init; } = (TaskStage)(-1);
    public TaskExecutionStatus Status { get; init; } = (TaskExecutionStatus)(-1);
    public string CurrentStep { get; init; } = "";
    public string ExpectedAction { get; init; } = "";

    public static TaskState Start(string description) => new()
    {
        TaskDescription = description,
        Stage = TaskStage.Planning,
        Status = TaskExecutionStatus.Active,
        CurrentStep = "определить план выполнения",
        ExpectedAction = "сформировать или подтвердить план"
    };

    public bool IsValid() => Enum.IsDefined(typeof(TaskStage), Stage)
        && Enum.IsDefined(typeof(TaskExecutionStatus), Status)
        && !string.IsNullOrWhiteSpace(TaskDescription)
        && !string.IsNullOrWhiteSpace(CurrentStep)
        && !string.IsNullOrWhiteSpace(ExpectedAction);

    public TaskState? TransitionTo(TaskStage target) => (Stage, target) switch
    {
        (TaskStage.Planning, TaskStage.Execution) or
        (TaskStage.Execution, TaskStage.Validation) or
        (TaskStage.Validation, TaskStage.Done) or
        (TaskStage.Validation, TaskStage.Execution) => this with { Stage = target },
        _ => null
    };
}
