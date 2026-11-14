using Reconstruction.Domain;
using Reconstruction.Domain.Models;
using Reconstruction.Domain.Policies;
using Xunit;

namespace Reconstruction.Tests;

public sealed class DomainRulesTests
{
    private static ReconstructionProject NewProject()
    {
        var proposal = new ReconstructionProposal(1, "Zona Norte", "Puente",
            DamageLevel.Destroyed, 1, 200m, ProposalPriority.High);
        return new ReconstructionProject(proposal);
    }

    private static ActivityDefinition Schedule() => new("Reparar puente", "Ingeniero",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), 120m);

    [Fact]
    public void ActivitiesAndReportsCannotBeModifiedThroughTheirPublicCollections()
    {
        var project = NewProject();
        project.SaveFormalization(200m, "Aportes", "Responsable", "Contratista", [Schedule()]);
        var activities = project.Activities;
        Assert.Throws<NotSupportedException>(() => ((ICollection<ProjectActivity>)activities).Clear());
        var activity = Assert.Single(activities);
        var reports = activity.Reports;
        Assert.Throws<NotSupportedException>(() => ((ICollection<ProgressReport>)reports).Clear());
        project.RegisterSignedContract("contrato://firmado", DateTimeOffset.UtcNow.AddMinutes(-1), "operador");
        project.RegisterProgress(activity.Id, ActivityStatus.InProgress, 10m, ["foto"], ["acta"], "operador");
        var report = Assert.Single(reports);
        Assert.Throws<NotSupportedException>(() => ((ICollection<ProgressReport>)reports).Add(report));
        Assert.Single(project.Activities);
        Assert.Single(activity.Reports);
    }

    [Fact]
    public void FundingReservationsUseExactMoneyAndReleaseOnlyTheBudgetDifference()
    {
        var reservation = new DisasterFundingReservation(1, 0m);
        reservation.Reserve(0m, 0.10m, 0.30m);
        reservation.Reserve(0m, 0.20m, 0.30m);
        Assert.Equal(0.30m, reservation.ReservedAmount);
        Assert.Throws<DomainException>(() => reservation.Reserve(0m, 0.01m, 0.30m));
        Assert.Equal(0.30m, reservation.ReservedAmount);
        reservation.Reserve(0.10m, 0.05m, 0.30m);
        Assert.Equal(0.25m, reservation.ReservedAmount);
    }

    [Fact]
    public void CannotCloseDraftOrWithoutActivities()
    {
        var p = NewProject();
        Assert.Throws<DomainException>(() => p.Close());
        p.SaveFormalization(200m, "Donaciones", "Autoridad", "Contratista", [Schedule()]);
        p.RegisterSignedContract("contrato://prueba-firmada", DateTimeOffset.UtcNow.AddMinutes(-1), "operador");
        Assert.Throws<DomainException>(() => p.Close());
    }

    [Fact]
    public void CannotCompleteActivityWithoutEvidenceAndDocuments()
    {
        var p = NewProject();
        p.SaveFormalization(200m, "Donaciones", "Autoridad", "Contratista", [Schedule()]);
        p.RegisterSignedContract("contrato://prueba-firmada", DateTimeOffset.UtcNow.AddMinutes(-1), "operador");
        var id = p.Activities.Single().Id;
        Assert.Throws<DomainException>(() => p.RegisterProgress(id, ActivityStatus.Completed,
            100m, [], [], "operador"));
    }

    [Fact]
    public void CanCloseOnlyAfterAllActivitiesComplete()
    {
        var p = NewProject();
        p.SaveFormalization(200m, "Donaciones", "Autoridad", "Contratista", [Schedule()]);
        p.RegisterSignedContract("contrato://prueba-firmada", DateTimeOffset.UtcNow.AddMinutes(-1), "operador");
        p.RegisterProgress(p.Activities.First().Id, ActivityStatus.Completed, 100m,
            ["https://example.org/foto.jpg"], ["https://example.org/acta.pdf"], "operador");
        p.Close();
        Assert.Equal(ProjectStatus.Closed, p.Status);
        Assert.NotNull(p.ClosedAt);
        Assert.Equal(5, p.Version);
    }

    [Fact]
    public void RejectsScheduleOverProjectBudget()
    {
        var p = NewProject();
        Assert.Throws<DomainException>(() => p.SaveFormalization(100m,
            "Donaciones", "Entidad", "Contratista", [Schedule()]));
    }

    [Fact]
    public void CannotRegisterCostsAboveActivityBudget()
    {
        var p = NewProject();
        p.SaveFormalization(200m, "Donaciones", "Autoridad", "Contratista", [Schedule()]);
        p.RegisterSignedContract("contrato://prueba-firmada", DateTimeOffset.UtcNow.AddMinutes(-1), "operador");
        Assert.Throws<DomainException>(() => p.RegisterProgress(p.Activities.First().Id,
            ActivityStatus.InProgress, 121m, [], [], "operador"));
    }
}
