namespace Reconstruction.Domain.Models;

// Una reserva compartida por todos los proyectos del mismo desastre.
public sealed class DisasterFundingReservation
{
    private DisasterFundingReservation() { }
    public int DisasterId { get; private set; }
    public decimal ReservedAmount { get; private set; }
    public long Version { get; private set; } = 1;

    public DisasterFundingReservation(int disasterId, decimal alreadyReserved)
    {
        if (disasterId <= 0 || alreadyReserved < 0) throw new DomainException("Reserva de financiación inválida.");
        DisasterId = disasterId; ReservedAmount = alreadyReserved;
    }

    public void EnsureAvailable(decimal previousBudget, decimal budget, decimal funding)
    {
        if (previousBudget < 0 || budget <= 0 || funding < 0 || ReservedAmount < previousBudget)
            throw new DomainException("Los montos de financiación y presupuesto no son válidos.");
        if (budget > funding - ReservedAmount + previousBudget)
            throw new DomainException("El presupuesto supera la financiación disponible del desastre, descontando los montos ya reservados.");
    }

    public void ReleaseUnspent(decimal amount)
    {
        if (amount < 0 || amount > ReservedAmount) throw new DomainException("Saldo a liberar inválido.");
        ReservedAmount -= amount;
        Version++;
    }

    public void Reserve(decimal previousBudget, decimal budget, decimal funding)
    {
        EnsureAvailable(previousBudget, budget, funding);
        ReservedAmount += budget - previousBudget;
        Version++;
    }
}
