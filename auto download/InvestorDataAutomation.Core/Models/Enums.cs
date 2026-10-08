namespace InvestorDataAutomation.Core.Models
{
    public enum CompanyStatus
    {
        Pending = 0,
        Processing = 1,
        Completed = 2,
        Partial = 3,
        Failed = 4,
        NeedsReview = 5
    }

    public enum DocumentType
    {
        AnnualReport,
        QuarterlyResults,
        FinancialResults,
        InvestorPresentation,
        EarningsPresentation,
        FinancialStatements,
        ShareholdingPattern,
        CorporateGovernance,
        ConcallTranscript,
        InvestorUpdate,
        CreditRating,
        PressRelease,
        Other
    }
}
