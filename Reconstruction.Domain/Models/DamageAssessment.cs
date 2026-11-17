namespace Reconstruction.Domain.Models;

public sealed record FundingAvailability(int DisasterId, decimal AvailableAmount, string Source, string SourceId = "donations", string Currency = "COP");
