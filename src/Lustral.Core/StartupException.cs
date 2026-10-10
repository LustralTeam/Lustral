namespace Lustral.Core;

/// <summary>
/// A startup failure whose message tells the user how to proceed. Logged without a stack trace.
/// </summary>
public sealed class StartupException(string message) : Exception(message);
