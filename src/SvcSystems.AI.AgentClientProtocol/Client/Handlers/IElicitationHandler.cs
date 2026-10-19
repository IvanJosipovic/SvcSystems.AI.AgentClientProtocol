namespace SvcSystems.AI.AgentClientProtocol;

/// <summary>Host-provided handlers for form and URL elicitation requests.</summary>
/// <remarks>
/// Requests and responses are the generated protocol models. Inspect
/// <see cref="CreateElicitationRequest.Value"/> for the <see cref="ElicitationFormMode"/>,
/// <see cref="ElicitationUrlMode"/>, or <see cref="CreateElicitationRequestCase2"/> payload and answer
/// with a <see cref="CreateElicitationResponse"/> case such as <see cref="ElicitationAcceptAction"/>.
/// </remarks>
public interface IElicitationHandler
{
    /// <summary>Whether the host can present form-based elicitation.</summary>
    bool SupportsForm { get; }

    /// <summary>Whether the host can present URL-based elicitation.</summary>
    bool SupportsUrl { get; }

    /// <summary>Requests user input for a form or URL interaction.</summary>
    /// <param name="request">The elicitation request; <see cref="CreateElicitationRequest.Value"/> carries the mode payload.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The action the user took, with the form answers when a form was accepted.</returns>
    ValueTask<CreateElicitationResponse> RequestElicitationAsync(CreateElicitationRequest request, CancellationToken cancellationToken = default);
}
