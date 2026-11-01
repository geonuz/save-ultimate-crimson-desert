using System.Windows.Controls;

namespace SaveUltimate.App;

public sealed class SkillsView : TabControl
{
    public SkillsView()
    {
        // Implicit styles match the exact type only; use the themed TabControl template.
        SetResourceReference(StyleProperty, typeof(TabControl));
        foreach (var (owner, name) in new[] { (0, Tr.Text("skills.character.0")), (1, Tr.Text("skills.character.1")), (2, Tr.Text("skills.character.2")) })
            Items.Add(new TabItem { Header = name, Content = new SkillPage(owner) });
    }
}
