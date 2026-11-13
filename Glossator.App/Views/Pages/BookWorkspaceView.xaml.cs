using System.Windows.Controls;
using System.Windows.Input;
using Glossator.App.ViewModels;

namespace Glossator.App.Views.Pages;

public partial class BookWorkspaceView : UserControl
{
    public BookWorkspaceView()
    {
        InitializeComponent();
    }

    private void TxtChapterRange_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is BookWorkspaceViewModel vm)
        {
            if (vm.ApplyRangeCommand.CanExecute(null))
            {
                vm.ApplyRangeCommand.Execute(null);
            }
            e.Handled = true;
        }
    }
}
