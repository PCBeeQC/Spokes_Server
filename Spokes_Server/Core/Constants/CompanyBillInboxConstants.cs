namespace Spokes_Server.Core.Constants;

public static class CompanyBillInboxConstants
{
    /// <summary>
    /// Reserved virtual owner ID for messages and folders belonging to the company billing mailbox.
    /// </summary>
    public const string SystemEmployeeId = "_company_bill_inbox";

    public const string DefaultFolder = "INBOX";

    public static class BillingStatus
    {
        public const string Unprocessed = "Unprocessed";
        public const string AutoDrafted = "AutoDrafted";
        public const string DuplicateDetected = "DuplicateDetected";
        public const string Statement = "Statement";
        public const string Receipt = "Receipt";
        public const string Approved = "Approved";
        public const string StatementConfirmed = "StatementConfirmed";
        public const string ReceiptConfirmed = "ReceiptConfirmed";
        public const string Dismissed = "Dismissed";
    }
}
