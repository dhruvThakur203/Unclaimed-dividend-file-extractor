using InvestorDataAutomation.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace InvestorDataAutomation.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<Company> Companies => Set<Company>();
        public DbSet<InvestorDocument> InvestorDocuments => Set<InvestorDocument>();
        public DbSet<CrawlLog> CrawlLogs => Set<CrawlLog>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Company>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.CompanyName).IsRequired().HasMaxLength(250);
                entity.Property(e => e.NSESymbol).HasMaxLength(50);
                entity.Property(e => e.BSECode).HasMaxLength(50);
                entity.Property(e => e.Website).HasMaxLength(500);
                entity.Property(e => e.InvestorPage).HasMaxLength(500);
                entity.Property(e => e.Status).HasConversion<string>();
                entity.HasIndex(e => e.CompanyName);
            });

            modelBuilder.Entity<InvestorDocument>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.DocumentTitle).IsRequired().HasMaxLength(500);
                entity.Property(e => e.DocumentUrl).IsRequired().HasMaxLength(2000);
                entity.Property(e => e.SHA256).HasMaxLength(100);
                entity.HasIndex(e => e.DocumentUrl);
                entity.HasIndex(e => e.SHA256);

                entity.HasOne(e => e.Company)
                      .WithMany(c => c.Documents)
                      .HasForeignKey(e => e.CompanyId)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<CrawlLog>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Url).IsRequired().HasMaxLength(2000);
                entity.HasOne(e => e.Company)
                      .WithMany(c => c.CrawlLogs)
                      .HasForeignKey(e => e.CompanyId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
