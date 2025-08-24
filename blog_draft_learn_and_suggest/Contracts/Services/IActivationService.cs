namespace blog_draft_learn_and_suggest.Contracts.Services;

public interface IActivationService
{
    Task ActivateAsync(object activationArgs);
}
