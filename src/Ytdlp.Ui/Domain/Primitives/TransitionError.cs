namespace Ytdlp.Ui.Domain.Primitives;

public enum TransitionError
{
    InvalidState,
    StaleAttempt,
    UnexpectedStage,
    ConfirmationRequired,
    InvalidAttempt,
    InvalidPublication,
    InvalidResumeState
}
