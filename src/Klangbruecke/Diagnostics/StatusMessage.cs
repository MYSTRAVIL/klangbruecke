namespace Klangbruecke.Diagnostics;

/// <summary>
/// One status event: the text the tray shows, and how serious the component that raised it says
/// it is.
///
/// The severity travels with the message because only the raiser knows it. StatusPresenter sees a
/// string, and asked to pick a level it could only guess from the wording - which is how a route
/// that failed to start came to be recorded at Error with a stack and then, one line later, at Info
/// describing the same event. Severities that track the presenter's ignorance rather than the event
/// are worse than no severities: a reader who greps [ERR] and finds half the story stops grepping.
///
/// Note what this deliberately does not carry: the exception. Components log that themselves through
/// <see cref="Log"/> before raising status, so the entry with the stack in it exists whether or not
/// anything is subscribed. Folding it in here would collapse the duplicate pair into one line, but
/// it would also make every error entry conditional on a live Status subscriber - and the failures
/// worth reading are the ones during startup and teardown, when there may not be one.
/// </summary>
/// <param name="Text">The message, in full. The tray truncates it; the log does not.</param>
/// <param name="Level">What the raiser says this is worth.</param>
public readonly record struct StatusMessage(string Text, LogLevel Level);
