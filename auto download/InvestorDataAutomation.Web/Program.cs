using System;
using System.IO;
using InvestorDataAutomation.Core.Interfaces;
using InvestorDataAutomation.Core.Options;
using InvestorDataAutomation.Data;
using InvestorDataAutomation.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog Setup
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("Logs/investor_automation_.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// Add options
builder.Services.Configure<CrawlOptions>(builder.Configuration.GetSection(CrawlOptions.Position));

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=investor_data.db";
var dbProvider = builder.Configuration["CrawlOptions:DatabaseProvider"] ?? "Sqlite";

builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    if (dbProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
    {
        options.UseSqlServer(connectionString);
    }
    else
    {
        options.UseSqlite(connectionString);
    }
});

// Add HTTP Clients and Services
builder.Services.AddHttpClient();
builder.Services.AddScoped<ICompanyWebsiteFinder, CompanyWebsiteFinder>();
builder.Services.AddScoped<IInvestorPageFinder, InvestorPageFinder>();
builder.Services.AddScoped<IWebsiteCrawler, WebsiteCrawler>();
builder.Services.AddSingleton<IDocumentDetector, DocumentDetector>();
builder.Services.AddSingleton<IDocumentClassifier, DocumentClassifier>();
builder.Services.AddSingleton<IFinancialPeriodDetector, FinancialPeriodDetector>();
builder.Services.AddSingleton<ITargetDocumentRelevanceFilter, TargetDocumentRelevanceFilter>();
builder.Services.AddScoped<IDocumentDownloader, DocumentDownloader>();
builder.Services.AddScoped<IDuplicateDetector, DuplicateDetector>();
builder.Services.AddScoped<ICrawlLogger, CrawlLogger>();
builder.Services.AddSingleton<IExcelImporter, ExcelImporter>();
builder.Services.AddSingleton<IExcelExporter, ExcelExporter>();
builder.Services.AddScoped<ICompanyProcessor, CompanyProcessor>();
builder.Services.AddSingleton<IJobQueueService, JobQueueService>();

// Add Controllers and Views
builder.Services.AddControllersWithViews();

var app = builder.Build();

// Ensure Database Created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.EnsureCreated();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

Log.Information("Investor Data Automation Application Starting...");
app.Run();
