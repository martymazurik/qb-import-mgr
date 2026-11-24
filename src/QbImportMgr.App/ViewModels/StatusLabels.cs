using QbImportMgr.Core.Domain;

namespace QbImportMgr.App.ViewModels;

public static class StatusLabels
{
    public static string Short(TxnStatus s) => s switch
    {
        TxnStatus.Duplicate => "Duplicate?",
        TxnStatus.InQb => "In QB",
        TxnStatus.Ambiguous => "Ambiguous",
        TxnStatus.HoldReceivePayment => "Hold: Rcv Pmt",
        TxnStatus.Paired => "Transfer (in)",
        _ => s.ToString(),
    };

    public static string Long(TxnStatus s) => s switch
    {
        TxnStatus.Duplicate => "Possible duplicate",
        TxnStatus.InQb => "Already in QuickBooks",
        TxnStatus.Ambiguous => "Ambiguous QuickBooks match",
        TxnStatus.HoldReceivePayment => "Waiting for Receive Payments",
        TxnStatus.Paired => "Transfer: other side carries the export",
        _ => s.ToString(),
    };
}
