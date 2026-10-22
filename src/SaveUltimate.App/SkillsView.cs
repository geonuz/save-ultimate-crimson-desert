using System.Windows.Controls;

namespace SaveUltimate.App;

public sealed class SkillsView : TabControl
{
    public SkillsView()
    {
        // Implicit styles match the exact type only; use the themed TabControl template.
        SetResourceReference(StyleProperty, typeof(TabControl));
        foreach (var (owner, name) in new[] { (0, Tr.Text("skills.character.0")), (2, Tr.Text("skills.character.2")), (1, Tr.Text("skills.character.1")) })
            Items.Add(new TabItem { Header = name, Content = new SkillPage(owner) });
    }
}
