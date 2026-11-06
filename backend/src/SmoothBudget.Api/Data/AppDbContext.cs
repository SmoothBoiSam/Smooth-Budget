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
    public DbSet<PlaidItem> PlaidItems => Set<PlaidItem>();
    public DbSet<BudgetMonthCategory> BudgetMonthCategories => Set<BudgetMonthCategory>();
    public DbSet<SweepTask> SweepTasks => Set<SweepTask>();
    public DbSet<DebtAccount> DebtAccounts => Set<DebtAccount>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Household>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.Property(x => x.PartnerName).HasMaxLength(100);
            e.HasMany(x => x.Users).WithOne(u => u.Household).HasForeignKey(u => u.HouseholdId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Account>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Mask).HasMaxLength(4);
            e.Property(x => x.PlaidAccountId).HasMaxLength(100);
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.PlaidItem).WithMany().HasForeignKey(x => x.PlaidItemId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.PlaidAccountId).IsUnique().HasFilter("\"PlaidAccountId\" IS NOT NULL");
        });

        b.Entity<PlaidItem>(e =>
        {
            e.Property(x => x.PlaidItemId).HasMaxLength(100);
            e.Property(x => x.InstitutionId).HasMaxLength(50);
            e.Property(x => x.InstitutionName).HasMaxLength(200);
            e.Property(x => x.StatusMessage).HasMaxLength(500);
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.PlaidItemId).IsUnique();
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
            e.Property(x => x.BankCategoryPrimary).HasMaxLength(60);
            e.Property(x => x.BankConfidence).HasMaxLength(20);
            e.Property(x => x.CategoryReason).HasMaxLength(200);
            e.Property(x => x.Fingerprint).HasMaxLength(64);
            e.Property(x => x.SplitFactor).HasPrecision(5, 4);
            e.HasIndex(x => new { x.AccountId, x.Fingerprint });
            e.Property(x => x.PlaidTransactionId).HasMaxLength(100);
            e.HasIndex(x => x.PlaidTransactionId).IsUnique().HasFilter("\"PlaidTransactionId\" IS NOT NULL");
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
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.HouseholdId, x.StartDate }).IsUnique();
            e.HasMany(x => x.Categories).WithOne().HasForeignKey(x => x.BudgetMonthId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BudgetMonthCategory>(e =>
        {
            // NoAction: categories and months both cascade from the household; one path is enough.
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.BudgetMonthId, x.CategoryId }).IsUnique();
        });

        b.Entity<SweepTask>(e =>
        {
            e.Property(x => x.Destination).HasMaxLength(120);
            e.HasOne(x => x.BudgetMonth).WithMany().HasForeignKey(x => x.BudgetMonthId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.HouseholdId, x.DoneUtc });
        });

        b.Entity<DebtAccount>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.HouseholdId);
            e.HasIndex(x => x.AccountId).IsUnique();
        });
    }
}
