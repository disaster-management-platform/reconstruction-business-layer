namespace Reconstruction.Application.Services;

internal static class ProjectVersionGuard
{
    public static void EnsureCurrent(long stored, long expected)
    {
        if (stored != expected) throw new BusinessConflictException(
            $"Versión obsoleta. Actual: {stored}; recibida: {expected}.");
    }
}
