using GameOrchestrator.Models;

namespace GameOrchestrator.Services;

public static class TaskPlanService
{
    // Only root entries are scheduler anchors. A group owns its children's schedule.
    public static IReadOnlyList<AutomationTaskConfig> Expand(IEnumerable<AutomationTaskConfig> anchors)
    {
        var plan = new List<AutomationTaskConfig>();
        foreach (var anchor in anchors.Where(item => item.Enabled))
        {
            if (anchor.IsGroup) plan.AddRange(GroupPlan(anchor, anchor.Children));
            else
            {
                anchor.IsGroupChild = false;
                anchor.IntervalAfterSeconds = 0;
                plan.Add(anchor);
            }
        }
        return plan.DistinctBy(item => item.Id).ToList();
    }

    private static IReadOnlyList<AutomationTaskConfig> GroupPlan(AutomationTaskConfig group, IEnumerable<AutomationTaskConfig> source)
    {
        var children = source.Where(item => item.Enabled && !item.IsGroup).ToList();
        for (var index = 0; index < children.Count; index++)
        {
            children[index].IsGroupChild = true;
            children[index].IntervalAfterSeconds = index < children.Count - 1 ? group.GroupTaskIntervalSeconds : 0;
        }
        return children;
    }

    public static IReadOnlyList<AutomationTaskConfig> ManualPlan(IEnumerable<AutomationTaskConfig> roots, AutomationTaskConfig selected)
    {
        var owner = roots.FirstOrDefault(item => item.IsGroup && item.Children.Contains(selected));
        if (owner is null) return Expand([selected]);
        if (!owner.Enabled) return [];
        return GroupPlan(owner, owner.Children.SkipWhile(item => item != selected));
    }
}
