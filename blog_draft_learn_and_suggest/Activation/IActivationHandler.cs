namespace blog_draft_learn_and_suggest.Activation;

public interface IActivationHandler
{
    bool CanHandle(object args);

    Task HandleAsync(object args);
}
