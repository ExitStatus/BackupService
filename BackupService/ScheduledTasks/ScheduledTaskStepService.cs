using BackupService.Database;
using BackupService.Enumerations;

namespace BackupService.ScheduledTasks
{
    /// <summary>
    /// Default <see cref="IScheduledTaskStepService"/>. A stateless helper over the tracked
    /// <see cref="ScheduledTask"/> entity graph (no DbContext), mirroring the profile child services.
    /// </summary>
    public sealed class ScheduledTaskStepService : IScheduledTaskStepService
    {
        public void Add(ScheduledTask task, IReadOnlyList<ScheduledTaskStepInput> inputs)
        {
            foreach (var input in inputs)
            {
                task.Steps.Add(NewStep(input));
            }
        }

        public IReadOnlyList<string> Sync(ScheduledTask task, IReadOnlyList<ScheduledTaskStepInput> inputs)
        {
            // Snapshot the original steps before mutating so we can describe what changed.
            var oldSteps = task.Steps.ToDictionary(
                s => s.Id,
                s => new StepSnapshot(s.Order, s.Name, s.Kind, s.Command, s.Arguments, s.Script, s.WorkingDirectory, s.RunViaShell));

            // Remove steps the user deleted (not present by id in the new set).
            var keptIds = inputs.Where(i => i.Id != 0).Select(i => i.Id).ToHashSet();
            foreach (var removed in task.Steps.Where(s => !keptIds.Contains(s.Id)).ToList())
            {
                task.Steps.Remove(removed);
            }

            // Update matched steps and add new ones.
            foreach (var input in inputs)
            {
                var existing = input.Id != 0
                    ? task.Steps.FirstOrDefault(s => s.Id == input.Id)
                    : null;

                if (existing is null)
                {
                    task.Steps.Add(NewStep(input));
                }
                else
                {
                    existing.Order = input.Order;
                    existing.Name = input.Name;
                    existing.Kind = input.Kind;
                    existing.Command = input.Command;
                    existing.Arguments = input.Arguments;
                    existing.Script = input.Script;
                    existing.WorkingDirectory = input.WorkingDirectory;
                    existing.RunViaShell = input.RunViaShell;
                }
            }

            return DescribeChanges(oldSteps, keptIds, inputs);
        }

        public IReadOnlyList<string> DescribeForCreateLog(IReadOnlyList<ScheduledTaskStepInput> inputs)
        {
            var lines = new List<string>(inputs.Count);
            foreach (var input in inputs.OrderBy(i => i.Order))
            {
                lines.Add($"Step {input.Order + 1}: {Describe(input)}");
            }
            return lines;
        }

        private static IReadOnlyList<string> DescribeChanges(
            IReadOnlyDictionary<int, StepSnapshot> oldSteps,
            IReadOnlySet<int> keptIds,
            IReadOnlyList<ScheduledTaskStepInput> inputs)
        {
            var changes = new List<string>();

            // Removed steps: present before, not kept by id now.
            foreach (var (stepId, old) in oldSteps)
            {
                if (!keptIds.Contains(stepId))
                {
                    changes.Add($"Step '{Label(old.Name, old.Command)}' removed");
                }
            }

            // Added or modified steps.
            foreach (var input in inputs)
            {
                if (input.Id == 0 || !oldSteps.TryGetValue(input.Id, out var old))
                {
                    changes.Add($"Step '{Label(input.Name, input.Command)}' added ({Describe(input)})");
                    continue;
                }

                if (old.Order != input.Order || old.Name != input.Name || old.Kind != input.Kind
                    || old.Command != input.Command || old.Arguments != input.Arguments
                    || old.Script != input.Script || old.WorkingDirectory != input.WorkingDirectory
                    || old.RunViaShell != input.RunViaShell)
                {
                    changes.Add($"Step '{Label(input.Name, input.Command)}' changed ({Describe(input)})");
                }
            }

            return changes;
        }

        private static ScheduledTaskStep NewStep(ScheduledTaskStepInput input) => new()
        {
            Order = input.Order,
            Name = input.Name,
            Kind = input.Kind,
            Command = input.Command,
            Arguments = input.Arguments,
            Script = input.Script,
            WorkingDirectory = input.WorkingDirectory,
            RunViaShell = input.RunViaShell,
        };

        // A human-readable one-liner for a step (for logs).
        private static string Describe(ScheduledTaskStepInput input)
        {
            var command = input.Kind == ScheduledTaskStepKind.PowerShell
                ? "PowerShell script"
                : input.RunViaShell
                    ? $"shell: {input.Command}"
                    : string.IsNullOrWhiteSpace(input.Arguments) ? input.Command ?? string.Empty : $"{input.Command} {input.Arguments}";
            return string.IsNullOrWhiteSpace(input.Name) ? command : $"{input.Name} — {command}";
        }

        private static string Label(string? name, string? command) =>
            !string.IsNullOrWhiteSpace(name) ? name : string.IsNullOrWhiteSpace(command) ? "PowerShell script" : command;

        private sealed record StepSnapshot(
            int Order, string? Name, ScheduledTaskStepKind Kind, string? Command, string? Arguments, string? Script, string? WorkingDirectory, bool RunViaShell);
    }
}
