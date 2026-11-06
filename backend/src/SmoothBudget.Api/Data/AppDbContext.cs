using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SmoothBudget.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Household> Households => Set<Household>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<BudgetMonth> BudgetMonths => Set<BudgetMonth>();
    public DbSet<CategoryRule> CategoryRules => Set<CategoryRule>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Household>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.HasMany(x => x.Users).WithOne(u => u.Household).HasForeignKey(u => u.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Account>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Mask).HasMaxLength(4);
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Category>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.SplitPercent).HasPrecision(5, 2);
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.HouseholdId, x.Name }).IsUnique();
        });

        b.Entity<Transaction>(e =>
        {
            e.Property(x => x.Merchant).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(300);
            e.Property(x => x.BankCategory).HasMaxLength(100);
            e.Property(x => x.Fingerprint).HasMaxLength(64);
            e.Property(x => x.SplitFactor).HasPrecision(5, 4);
            e.HasIndex(x => new { x.AccountId, x.Fingerprint });
            // No cascade: undoing an import deletes its rows explicitly (and keeps SQL Server's single-cascade-path rule happy).
            e.HasOne<ImportBatch>().WithMany().HasForeignKey(x => x.ImportBatchId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            // The Home summary reads one household's transactions for one budget month.
            e.HasIndex(x => new { x.HouseholdId, x.Date });
        });

        b.Entity<CategoryRule>(e =>
        {
            e.Property(x => x.Pattern).HasMaxLength(200);
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.HouseholdId, x.Pattern }).IsUnique();
        });

        b.Entity<ImportBatch>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(200);
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BudgetMonth>(e =>
        {
            e.Property(x => x.SweepDestination).HasMaxLength(100);
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.HouseholdId, x.StartDate }).IsUnique();
        });
    }
}
