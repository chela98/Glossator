using System.Windows.Controls;
using Glossator.App.ViewModels;

namespace Glossator.App.Views.Pages;

public partial class GlossaryView : UserControl
{
    public GlossaryView()
    {
        InitializeComponent();
    }

    private void GridGlossary_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is GlossaryViewModel vm)
        {
            vm.UpdateSelection(GridGlossary.SelectedItems.OfType<GlossaryTermViewModel>());
        }
    }
}
