using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace SmoothBudget.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    /// <summary>The keys that sign login cookies, kept in the database so you stay signed in across deploys.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<Household> Households => Set<Household>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<BudgetMonth> BudgetMonths => Set<BudgetMonth>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<PlaidItem> PlaidItems => Set<PlaidItem>();
    public DbSet<BudgetMonthCategory> BudgetMonthCategories => Set<BudgetMonthCategory>();
    public DbSet<SweepTask> SweepTasks => Set<SweepTask>();
    public DbSet<DebtAccount> DebtAccounts => Set<DebtAccount>();
    public DbSet<RecurringItem> RecurringItems => Set<RecurringItem>();
    public DbSet<PartnerArrangement> PartnerArrangements => Set<PartnerArrangement>();
    public DbSet<SavingsGoal> SavingsGoals => Set<SavingsGoal>();
    public DbSet<PayProfile> PayProfiles => Set<PayProfile>();
    public DbSet<ScheduleMonth> ScheduleMonths => Set<ScheduleMonth>();
    public DbSet<PayStub> PayStubs => Set<PayStub>();
    public DbSet<PayPeriodForecast> PayPeriodForecasts => Set<PayPeriodForecast>();
    public DbSet<ExpectedIncome> ExpectedIncomes => Set<ExpectedIncome>();
    public DbSet<PaydayChecklistItem> PaydayChecklistItems => Set<PaydayChecklistItem>();
    public DbSet<PushDevice> PushDevices => Set<PushDevice>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<Friend> Friends => Set<Friend>();
    public DbSet<FriendInvite> FriendInvites => Set<FriendInvite>();
    public DbSet<HouseholdLink> HouseholdLinks => Set<HouseholdLink>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Household>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.Property(x => x.PartnerName).HasMaxLength(100);
            e.Property(x => x.DisplayName).HasMaxLength(60);
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
            e.HasOne(x => x.Friend).WithMany().HasForeignKey(x => x.FriendId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.DebtAccount).WithMany().HasForeignKey(x => x.DebtAccountId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.TransferAccount).WithMany().HasForeignKey(x => x.TransferAccountId).OnDelete(DeleteBehavior.SetNull);
            // No cascade: undoing an import deletes its rows explicitly (and keeps SQL Server's single-cascade-path rule happy).
            e.HasOne<ImportBatch>().WithMany().HasForeignKey(x => x.ImportBatchId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            // The Home summary reads one household's transactions for one budget month.
            e.HasIndex(x => new { x.HouseholdId, x.Date });
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

        b.Entity<PayProfile>(e => e.HasIndex(x => x.HouseholdId).IsUnique());
        b.Entity<ScheduleMonth>(e =>
        {
            e.Property(x => x.Source).HasMaxLength(10);
            e.HasIndex(x => new { x.HouseholdId, x.Year, x.Month }).IsUnique();
        });
        b.Entity<PayStub>(e => e.HasIndex(x => new { x.HouseholdId, x.PayDate }).IsUnique());
        b.Entity<PayPeriodForecast>(e =>
        {
            e.Property(x => x.ActualSource).HasMaxLength(10);
            e.HasIndex(x => new { x.HouseholdId, x.PayDate }).IsUnique();
        });
        b.Entity<ExpectedIncome>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => new { x.HouseholdId, x.ExpectedDate });
        });
        b.Entity<PaydayChecklistItem>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(200);
            e.HasIndex(x => new { x.HouseholdId, x.PayDate, x.Key }).IsUnique();
        });

        b.Entity<PushDevice>(e =>
        {
            e.Property(x => x.Endpoint).HasMaxLength(1000);
            e.Property(x => x.P256dh).HasMaxLength(200);
            e.Property(x => x.Auth).HasMaxLength(100);
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.Endpoint).IsUnique();
        });
        b.Entity<NotificationLog>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(200);
            e.HasOne<Household>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.HouseholdId, x.Key }).IsUnique();
        });

        b.Entity<SavingsGoal>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(120);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.HouseholdId);
        });

        b.Entity<Friend>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(60);
            e.Property(x => x.TransferName).HasMaxLength(100);
            e.HasIndex(x => x.HouseholdId);
            e.HasOne<HouseholdLink>().WithMany().HasForeignKey(x => x.LinkId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<FriendInvite>(e =>
        {
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.HouseholdId);
            e.HasOne<Friend>().WithMany().HasForeignKey(x => x.FriendId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<HouseholdLink>(e =>
        {
            e.HasIndex(x => x.HouseholdAId);
            e.HasIndex(x => x.HouseholdBId);
        });

        b.Entity<PartnerArrangement>(e =>
        {
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Friend).WithMany().HasForeignKey(x => x.FriendId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.HouseholdId);
        });

        b.Entity<RecurringItem>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(200);
            e.Property(x => x.Name).HasMaxLength(120);
            e.HasIndex(x => new { x.HouseholdId, x.Key }).IsUnique();
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
