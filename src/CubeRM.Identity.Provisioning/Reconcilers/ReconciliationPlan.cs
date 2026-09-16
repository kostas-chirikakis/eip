namespace CubeRM.Identity.Provisioning.Reconcilers;

/// <summary>
/// The diff between declared and actual state, rendered for human review before apply.
/// </summary>
/// <remarks>
/// Posted as a pull request comment. Nothing is applied without a reviewed plan — in a
/// shared tenant, an unreviewed change is a change made directly against the environment
/// serving all 30 customers.
/// </remarks>
public sealed record ReconciliationPlan(
    string Slug,
    string TargetTenant,
    IReadOnlyList<PlannedChange> Changes)
{
    public bool HasChanges => Changes.Count > 0;

    /// <summary>
    /// Deletions require an explicit <c>--confirm-destructive</c>, which CI supplies only
    /// for an offboarding-labelled pull request. RT-4 — an operator deleting the wrong
    /// customer — is a real risk when 30 customers' objects sit side by side.
    /// </summary>
    public bool HasDestructiveChanges => Changes.Any(c => c.Action is ChangeAction.Delete);

    public string ToMarkdown()
    {
        if (!HasChanges)
            return $"### `{Slug}` → `{TargetTenant}`\n\nNo changes. Already reconciled.";

        var lines = new List<string>
        {
            $"### `{Slug}` → `{TargetTenant}`",
            "",
            $"{Changes.Count} change(s)" + (HasDestructiveChanges ? " — **includes deletions**" : ""),
            "",
            "| Action | Resource | Key | Detail |",
            "| --- | --- | --- | --- |"
        };

        lines.AddRange(Changes.Select(c =>
            $"| {Icon(c.Action)} {c.Action} | {c.ResourceType} | `{c.IdempotencyKey}` | {c.Detail} |"));

        if (HasDestructiveChanges)
        {
            lines.Add("");
            lines.Add("> ⚠ This plan deletes objects. Applying it requires `--confirm-destructive`, " +
                      "which CI supplies only for an offboarding-labelled PR. Deletions are soft " +
                      "first, with a 30-day lag before hard delete.");
        }

        return string.Join('\n', lines);
    }

    private static string Icon(ChangeAction action) => action switch
    {
        ChangeAction.Create => "🟢",
        ChangeAction.Update => "🟡",
        ChangeAction.Delete => "🔴",
        _ => "⚪"
    };
}

/// <param name="IdempotencyKey">
/// Natural key. Everything is upsert-by-natural-key, so a re-run is a no-op and a partial
/// failure is resumable. A reconciler that cannot safely be re-run is one people avoid
/// running.
/// </param>
public sealed record PlannedChange(
    ChangeAction Action,
    string ResourceType,
    string IdempotencyKey,
    string Detail);

public enum ChangeAction { NoOp, Create, Update, Delete }
