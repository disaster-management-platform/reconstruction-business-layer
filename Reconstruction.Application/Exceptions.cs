namespace Reconstruction.Application;
public sealed class MissingResourceException(string message) : Exception(message);
public sealed class BusinessConflictException(string message) : Exception(message);
public sealed class DependencyUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
