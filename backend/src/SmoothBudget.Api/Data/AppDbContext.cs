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
            e.Property(x => x.SplitFactor).HasPrecision(5, 4);
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            // The Home summary reads one household's transactions for one budget month.
            e.HasIndex(x => new { x.HouseholdId, x.Date });
        });

        b.Entity<BudgetMonth>(e =>
        {
            e.Property(x => x.SweepDestination).HasMaxLength(100);
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.HouseholdId, x.StartDate }).IsUnique();
        });
    }
}
