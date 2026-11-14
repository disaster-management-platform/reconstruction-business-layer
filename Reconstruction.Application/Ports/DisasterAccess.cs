namespace Reconstruction.Application.Ports;

public interface IDisasterAccess
{
    // Null means a trusted background process or an explicitly configured local demonstration.
    IReadOnlyCollection<int>? AllowedDisasters { get; }
    bool CanAccess(int disasterId) => AllowedDisasters is null || AllowedDisasters.Contains(disasterId);
}
public sealed class UnrestrictedDisasterAccess : IDisasterAccess
{
    public IReadOnlyCollection<int>? AllowedDisasters => null;
}
