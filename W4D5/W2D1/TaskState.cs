using System.Text.Json;
using System.Text.Json.Serialization;

namespace W2D1;

internal enum TaskStage { Planning, Execution, Validation, Done }
internal enum TaskExecutionStatus { Active, Paused }

// Изменения создают копию до записи на диск. Восстановление JSON не является переходом.
internal sealed record TaskState
{
    private TaskStage _stage = TaskStage.Planning;
    public string TaskDescription { get; init; } = "";
    [JsonInclude]
    public TaskStage Stage
    {
        get => _stage;
        private init => _stage = Enum.IsDefined(typeof(TaskStage), value)
            ? value : throw new JsonException("Неизвестный Stage.");
    }
    public TaskExecutionStatus Status { get; init; } = (TaskExecutionStatus)(-1);
    public string CurrentStep { get; init; } = "";
    public string ExpectedAction { get; init; } = "";
    [JsonInclude]
    public bool PlanApproved { get; private init; }
    [JsonInclude]
    public bool ExecutionCompleted { get; private init; }
    [JsonInclude]
    public bool ValidationPassed { get; private init; }

    public static TaskState Start(string description) => new()
    {
        TaskDescription = description,
        Status = TaskExecutionStatus.Active,
        CurrentStep = "определить план выполнения",
        ExpectedAction = "сформировать план и утвердить его: /task approve-plan"
    };

    public bool IsValid() => Enum.IsDefined(typeof(TaskStage), Stage)
        && Enum.IsDefined(typeof(TaskExecutionStatus), Status)
        && !string.IsNullOrWhiteSpace(TaskDescription)
        && !string.IsNullOrWhiteSpace(CurrentStep)
        && !string.IsNullOrWhiteSpace(ExpectedAction);

    // Единственный набор правил: направление, назначение команды, пауза и подтверждения.
    public bool CanTransition(TaskStage target, out string error, bool revision = false)
    {
        var failure = (Stage, target) switch
        {
            (TaskStage.Planning, TaskStage.Execution) => PlanApproved ? null
                : "Причина: план ещё не утверждён.\nНеобходимое действие: /task approve-plan",
            (TaskStage.Execution, TaskStage.Validation) => ExecutionCompleted ? null
                : "Причина: выполнение текущего этапа не завершено.\nНеобходимое действие: /task complete-execution",
            (TaskStage.Validation, TaskStage.Done) => ValidationPassed ? null
                : "Причина: успешная валидация не подтверждена.\nНеобходимое действие: /task validation pass после успешной проверки; для доработки — /task revise",
            (TaskStage.Validation, TaskStage.Execution) => null,
            _ => "Причина: такой переход не предусмотрен.\nНеобходимое действие: используйте /task next по порядку этапов или /task revise из Validation; для новой задачи — /task start <описание>"
        };
        if (revision && Stage != TaskStage.Validation)
            failure = "Причина: /task revise доступна только из Validation.\nНеобходимое действие: используйте /task next с подтверждениями текущего этапа; для новой задачи — /task start <описание>";
        if (Status == TaskExecutionStatus.Paused)
            failure = "Причина: задача приостановлена.\nНеобходимое действие: /task resume, затем подтвердите условия текущего этапа";
        error = failure is null ? "" : $"Переход {Stage} → {target} запрещён.\n{failure}";
        return failure is null;
    }

    public TaskState? TryTransition(TaskStage target, out string error, bool revision = false)
    {
        if (!CanTransition(target, out error, revision)) return null;
        return this with
        {
            Stage = target,
            ExecutionCompleted = target == TaskStage.Execution ? false : ExecutionCompleted,
            ValidationPassed = target is TaskStage.Execution or TaskStage.Validation ? false : ValidationPassed
        };
    }

    public TaskState? Confirm(string command, out string error)
    {
        var requiredStage = command switch
        {
            "approve-plan" => TaskStage.Planning,
            "complete-execution" => TaskStage.Execution,
            "validation pass" or "validation fail" => TaskStage.Validation,
            _ => throw new ArgumentException("Неизвестная команда подтверждения.", nameof(command))
        };
        error = Status == TaskExecutionStatus.Paused
            ? "Задача приостановлена. Необходимое действие: /task resume."
            : Stage != requiredStage ? $"Команда /task {command} доступна только в {requiredStage}. Текущий Stage = {Stage}. Используйте /task для просмотра состояния и /task next для последовательного перехода."
            : "";
        if (error.Length != 0) return null;
        return command switch
        {
            "approve-plan" => this with { PlanApproved = true },
            "complete-execution" => this with { ExecutionCompleted = true },
            "validation pass" => this with { ValidationPassed = true },
            _ => this with { ValidationPassed = false }
        };
    }
}
