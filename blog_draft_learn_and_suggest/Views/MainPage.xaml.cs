using Microsoft.UI.Xaml.Controls;

using blog_draft_learn_and_suggest.ViewModels;

namespace blog_draft_learn_and_suggest.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel
    {
        get;
    }

    public MainPage()
    {
        ViewModel = App.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }
}
