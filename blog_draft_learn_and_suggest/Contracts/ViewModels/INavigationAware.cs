namespace blog_draft_learn_and_suggest.Contracts.ViewModels;

public interface INavigationAware
{
    void OnNavigatedTo(object parameter);

    void OnNavigatedFrom();
}
