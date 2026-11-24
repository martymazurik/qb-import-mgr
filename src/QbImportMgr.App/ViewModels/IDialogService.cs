namespace QbImportMgr.App.ViewModels;

public interface IDialogService
{
    void Info(string message, string title = "qb-import-mgr");
    void Error(string message, string title = "qb-import-mgr");
    bool Confirm(string message, string title = "qb-import-mgr");
    string? OpenFile(string title, string filter);
    void ShowText(string title, string text);
    bool ShowAccountMapping(AccountMappingViewModel vm);
    bool ShowRuleEditor(RuleEditorViewModel vm);
    void ShowExport(ExportViewModel vm);
    void ShowBatches(BatchesViewModel vm);
    void OpenFolder(string path);
}
